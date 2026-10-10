using System.Threading.Channels;
using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Expansion;
using TextFlow.Core.Input;
using TextFlow.Core.Menus;
using TextFlow.Core.Security;
using TextFlow.Core.Templates;

namespace TextFlow.Core.Engine;

/// <summary>
/// Orchestrates hook → trigger → group menu or snippet → insertion → chime (spec §8, ADR-0007/0008).
/// Every event is handled on one loop, in order, so the state (open menu, pending trigger) needs no locks.
/// Hook events take priority over internal work (menu results, timeouts, pause) so a typed key is never
/// overtaken by an older timeout.
/// </summary>
/// <remarks>Fails closed: capture is off while paused, in excluded apps, without a target and after the loop stops.</remarks>
public sealed partial class ExpansionEngine
{
    private readonly IInputHook _hook;
    private readonly ITargetResolver _resolver;
    private SecurityPolicy _policy; // replaced from Configuración (H4.3): read with Volatile
    private readonly ITextInsertionService _insertion;
    private readonly IMenuPresenter _menu;
    private readonly IExpansionFeedback _feedback;
    private readonly IFieldPrompt? _fieldPrompt;
    private readonly IVariableSource _variables;
    private readonly IUsageRecorder? _usage;
    private readonly IPointerLocator _pointer;
    private readonly IDiagnosticSink _sink;
    private readonly TimeProvider _time;
    private ExpansionEngineOptions _options; // pending timeout changes from Configuración (H4.3)
    private readonly Channel<EngineWork> _inbox = Channel.CreateUnbounded<EngineWork>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<Barrier> _barriers = [];

    private volatile LibraryIndex? _index;
    private volatile bool _paused;
    private OpenMenu? _open;
    private int _menuSession;
    private MenuCloseReason? _closeReason;
    private ITimer? _pendingTimer;
    private ITimer? _menuDelayTimer;
    private CancellationToken _stopping;

    public ExpansionEngine(
        IInputHook hook,
        ITargetResolver resolver,
        SecurityPolicy policy,
        ITextInsertionService insertion,
        IMenuPresenter menu,
        IExpansionFeedback feedback,
        IPointerLocator pointerLocator,
        IDiagnosticSink sink,
        TimeProvider time,
        ExpansionEngineOptions options,
        IFieldPrompt? fieldPrompt = null,
        IVariableSource? variables = null,
        IUsageRecorder? usage = null)
    {
        _hook = hook;
        _resolver = resolver;
        _policy = policy;
        _insertion = insertion;
        _menu = menu;
        _feedback = feedback;
        _pointer = pointerLocator;
        _sink = sink;
        _time = time;
        _options = options;
        _fieldPrompt = fieldPrompt;
        _variables = variables ?? new ClockVariables(time);
        _usage = usage;
    }

    public bool IsPaused => _paused;

    /// <summary>Shortest and longest wait for an ambiguous trigger that Configuración allows.</summary>
    public static readonly TimeSpan MinPendingTimeout = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc cref="MinPendingTimeout"/>
    public static readonly TimeSpan MaxPendingTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Applies to the next ambiguous trigger (any thread).</summary>
    public void SetPendingTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, MinPendingTimeout);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, MaxPendingTimeout);
        Volatile.Write(ref _options, Volatile.Read(ref _options) with { PendingTimeout = timeout });
    }

    /// <summary>Replaces the exclusion rules; the next trigger is judged by them (any thread).</summary>
    public void UsePolicy(SecurityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Volatile.Write(ref _policy, policy);
    }

    /// <summary>Installs a library (startup or hot reload). Triggers of the previous one stop firing.</summary>
    public async Task LoadAsync(LibraryIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
        await _hook.ReplaceTriggersAsync(index.Triggers).ConfigureAwait(false);
    }

    /// <summary>Stops capturing at once (any thread). An open menu closes; triggers already queued do not expand.</summary>
    public void Pause()
    {
        _paused = true;
        _hook.CaptureEnabled = false;
        _inbox.Writer.TryWrite(new PauseChanged(Paused: true));
    }

    public void Resume()
    {
        _paused = false;
        _inbox.Writer.TryWrite(new PauseChanged(Paused: false));
    }

    /// <summary>
    /// Command palette (D11), step 1: the field the user is in, captured (and bookmarked so the focus can come back to
    /// it) before the palette window takes the focus. Null when nothing is focused or UI Automation did not answer.
    /// </summary>
    public async Task<ActiveTarget?> CapturePaletteTargetAsync()
    {
        var target = await CaptureTargetAsync(locateCaret: true).ConfigureAwait(false); // a template opens its fields there
        if (target is not null)
        {
            _resolver.BookmarkField(target);
        }

        return target;
    }

    /// <summary>Command palette, step 2 (any thread): insert this snippet into the field captured in step 1.</summary>
    public void InsertFromPalette(string snippetId, ActiveTarget target)
    {
        ArgumentException.ThrowIfNullOrEmpty(snippetId);
        ArgumentNullException.ThrowIfNull(target);
        _inbox.Writer.TryWrite(new PaletteChosen(snippetId, target));
    }

    /// <summary>
    /// Same checks as a typed trigger, minus the abbreviation to delete: the original field must come back and pass
    /// the policy (never into a password field), or nothing is inserted. An explicit choice works while paused.
    /// </summary>
    private async Task InsertFromPaletteAsync(PaletteChosen chosen)
    {
        if (_index?.FindById(chosen.SnippetId) is not { } snippet)
        {
            return; // the library changed while the palette was open
        }

        CancelMenu(reason: null);
        DisposePendingTimer();
        var (target, _) = await ReturnToAsync(chosen.Target).ConfigureAwait(false);
        if (target is null)
        {
            Record(new TargetRejected(Now, chosen.Target.ProcessName, RejectionReason.WindowChanged));
            return;
        }

        if (!Volatile.Read(ref _policy).Evaluate(target, TextFlowFeature.Expansion).IsAllowed)
        {
            Record(new TargetRejected(Now, target.ProcessName, RejectionReason.PolicyDenied));
            return;
        }

        await ExpandContentAsync(target, snippet, backspaces: 0, trailing: string.Empty, fromMenu: false).ConfigureAwait(false);
    }

    /// <summary>Completes once every event queued before the call (and the work it caused) has been handled.</summary>
    public Task IdleAsync()
    {
        var barrier = new Barrier(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_inbox.Writer.TryWrite(barrier))
        {
            barrier.Done.TrySetResult();
        }

        return barrier.Done.Task;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _stopping = ct;
        _menu.Finished += OnMenuFinished;
        if (_fieldPrompt is not null)
        {
            _fieldPrompt.Finished += OnFieldsFinished;
        }

        try
        {
            Record(new EngineStateChanged(Now, EngineState.Starting));
            await RefreshCaptureAsync().ConfigureAwait(false);
            Record(new EngineStateChanged(Now, _paused ? EngineState.Paused : EngineState.Running));

            while (await NextAsync(ct).ConfigureAwait(false) is { } work)
            {
                await HandleSafelyAsync(work).ConfigureAwait(false);
            }

            Record(new EngineFault(Now, nameof(ChannelClosedException))); // the hook stopped: nothing more can expand
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>Next hook event or inbox work; null when the hook channel closed.</summary>
    private async Task<object?> NextAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_hook.Events.TryRead(out var hookEvent))
            {
                return hookEvent;
            }

            if (_inbox.Reader.TryRead(out var work))
            {
                if (work is Barrier barrier)
                {
                    _barriers.Add(barrier);
                    continue;
                }

                return work;
            }

            // Both queues are empty and nothing is being handled: everyone waiting for idle can go.
            ReleaseBarriers();

            if (!await WaitForWorkAsync(ct).ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    /// <summary>Waits for either queue with one waiter each, cancelling the loser so waiters never pile up.</summary>
    /// <returns>False when the hook channel completed.</returns>
    private async Task<bool> WaitForWorkAsync(CancellationToken ct)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hookReady = _hook.Events.WaitToReadAsync(wake.Token).AsTask();
        var inboxReady = _inbox.Reader.WaitToReadAsync(wake.Token).AsTask();
        var first = await Task.WhenAny(hookReady, inboxReady).ConfigureAwait(false);
        await wake.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        return first != hookReady || await hookReady.ConfigureAwait(false);
    }

    private void ReleaseBarriers()
    {
        foreach (var barrier in _barriers)
        {
            barrier.Done.TrySetResult();
        }

        _barriers.Clear();
    }

    /// <summary>A failure in one event (UIA, insertion) must not kill the engine: close the menu and keep going.</summary>
    private async Task HandleSafelyAsync(object work)
    {
        try
        {
            await HandleAsync(work).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record(new EngineFault(Now, ex.GetType().Name));
            CancelMenu(reason: null);
        }
    }

    private Task HandleAsync(object work) => work switch
    {
        TriggerPending pending => HandlePendingAsync(pending),
        TriggerTyped typed => HandleTriggerAsync(typed.Match, typed.ForegroundWindow),
        MenuKeyPressed key when _open is not null => SendToMenu(key.Input),
        MenuInterrupted interrupted when _open is not null => InterruptMenu(interrupted),
        ForegroundChanged or FocusChanged when _waiting is not null => FocusMovedWhileWaitingAsync((HookEvent)work),
        PointerReleased when _waiting is not null => DeliverIfBackAsync(),
        ForegroundChanged => HandleForegroundChangedAsync(),
        FocusChanged { KnownField: true } when _open is null => RecheckKnownFieldAsync(),
        FocusChanged when _open is null => RefreshCaptureUnlessSupersededAsync(),
        TypingActivity => WarmFeedback(),
        MenuFinished finished when _open?.Session == finished.Session => FinishMenuAsync(finished.Step),
        MenuDelayElapsed elapsed when _open is { Shown: false } && _open.Session == elapsed.Session => RevealMenu(),
        FieldsFinished fields => FinishFieldsAsync(fields),
        WaitElapsed elapsed => GiveUpWaitingAsync(elapsed),
        PaletteChosen chosen => InsertFromPaletteAsync(chosen),
        PendingElapsed elapsed => _hook.FlushPendingAsync(elapsed.Version),
        PauseChanged change => ApplyPauseAsync(change.Paused),
        _ => Task.CompletedTask, // stale menu result, or menu input with no menu
    };

    private Task WarmFeedback()
    {
        _feedback.Warm();
        return Task.CompletedTask;
    }

    private Task HandlePendingAsync(TriggerPending pending)
    {
        if (!_paused && _index?.FindMenu(pending.Match.SnippetId) is not null)
        {
            // Menus open at once; a key that continues a longer trigger ("cp" + '1') still fires it.
            return HandleTriggerAsync(pending.Match, pending.ForegroundWindow);
        }

        _pendingTimer?.Dispose();
        _pendingTimer = _time.CreateTimer(
            _ => _inbox.Writer.TryWrite(new PendingElapsed(pending.Version)), null, Volatile.Read(ref _options).PendingTimeout, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    private async Task HandleTriggerAsync(TriggerMatch match, nint foregroundWindow)
    {
        CancelMenu(MenuCloseReason.LongerTrigger); // "cp" menu was open and the user completed "cp1"
        DisposePendingTimer();

        var index = _index;
        // Plain text needs no caret (only menus and field prompts are placed there): skip that lookup, expand sooner.
        var plainText = index?.FindSnippet(match.SnippetId) is { } found && !found.Content.Contains("{{", StringComparison.Ordinal);
        var target = await CaptureTargetAsync(locateCaret: !plainText).ConfigureAwait(false);
        if (Reject(target, foregroundWindow) is { } reason)
        {
            Record(new TargetRejected(Now, target?.ProcessName ?? string.Empty, reason));
            if (reason != RejectionReason.WindowChanged)
            {
                await ReEmitAsync(target, match.Delimiter).ConfigureAwait(false); // never into a window the user did not type in
            }

            return;
        }

        if (_paused)
        {
            await ReEmitAsync(target, match.Delimiter).ConfigureAwait(false); // typed before the pause: give the key back
        }
        else if (index?.FindSnippet(match.SnippetId) is { } snippet)
        {
            await ExpandSnippetAsync(target!, snippet, match).ConfigureAwait(false);
        }
        else if (index?.FindMenu(match.SnippetId) is { } menu)
        {
            await OpenMenuAsync(target!, menu, match).ConfigureAwait(false);
        }
        else
        {
            await ReEmitAsync(target, match.Delimiter).ConfigureAwait(false); // library reloaded meanwhile
        }
    }

    private RejectionReason? Reject(ActiveTarget? target, nint foregroundWindow)
    {
        if (target is null)
        {
            return RejectionReason.NoTarget; // the swallowed key is lost: there is nowhere safe to type it
        }

        if (target.WindowHandle != foregroundWindow)
        {
            return RejectionReason.WindowChanged;
        }

        return Volatile.Read(ref _policy).Evaluate(target, TextFlowFeature.Expansion).IsAllowed ? null : RejectionReason.PolicyDenied;
    }

    private async Task ExpandSnippetAsync(ActiveTarget target, MenuSnippetEntry snippet, TriggerMatch match)
    {
        // A pending trigger broken by another key carries that key as Delimiter: type it after the expansion.
        var trailing = match.Delimiter is { } key and not ('\r' or '\t') ? key.ToString() : string.Empty;
        var result = await ExpandContentAsync(target, snippet, match.Backspaces, trailing, fromMenu: false).ConfigureAwait(false);
        if (result is { Succeeded: false, InputSent: false })
        {
            await ReEmitAsync(target, match.Delimiter).ConfigureAwait(false);
        }
    }

    private async Task OpenMenuAsync(ActiveTarget target, GroupMenu menu, TriggerMatch match)
    {
        if (match.Delimiter is not null)
        {
            // "cp" + another key: the user kept typing, so no menu (same as a menu dismissed by that key).
            await ReEmitAsync(target, match.Delimiter).ConfigureAwait(false);
            return;
        }

        _open = new OpenMenu(target, match, menu, ++_menuSession);
        _closeReason = null;
        _hook.MenuMode = true; // at once: a choice typed from memory ("lc" + '1') must not reach the app
        var delay = Volatile.Read(ref _options).MenuDelay;
        if (delay <= TimeSpan.Zero)
        {
            await RevealMenu().ConfigureAwait(false);
            return;
        }

        var session = _open.Session;
        DisposeMenuDelayTimer();
        _menuDelayTimer = _time.CreateTimer(
            _ => _inbox.Writer.TryWrite(new MenuDelayElapsed(session)), null, delay, Timeout.InfiniteTimeSpan);
    }

    private Task RevealMenu()
    {
        DisposeMenuDelayTimer();
        var open = _open!;
        _open = open with { Shown = true };
        var caret = open.Target.Control.CaretBounds;
        _menu.Show(open.Menu, caret ?? _pointer.CursorAnchor(), open.Target.Monitor, open.Session);
        Record(new MenuShown(Now, open.Target.ProcessName, AnchoredToCaret: caret is not null));
        return Task.CompletedTask;
    }

    private async Task SendToMenu(MenuInput input)
    {
        if (_open is { Shown: false })
        {
            if (input.Kind == MenuInputKind.Escape)
            {
                DropUnshownMenu(); // nothing on screen to close
                return;
            }

            await RevealMenu().ConfigureAwait(false); // a choice typed from memory
        }

        _feedback.Warm(); // menu keys are swallowed (no TypingActivity): keep the audio device awake for the choice
        _menu.Send(input);
    }

    /// <summary>The user went on before the menu was drawn: leave menu mode as if it never opened (no diagnostics).</summary>
    private void DropUnshownMenu()
    {
        DisposeMenuDelayTimer();
        _open = null;
        _hook.MenuMode = false;
    }

    private Task InterruptMenu(MenuInterrupted interrupted)
    {
        if (_open is { Shown: false })
        {
            DropUnshownMenu(); // typing on through "dirección", or a click elsewhere
            return Task.CompletedTask;
        }

        if (interrupted is { ClickX: { } x, ClickY: { } y })
        {
            if (_menu.Contains(x, y))
            {
                return Task.CompletedTask; // clicking an entry is the menu's own business
            }

            _closeReason = MenuCloseReason.ClickOutside;
        }
        else
        {
            _closeReason = MenuCloseReason.OtherKey;
        }

        _menu.Dismiss(); // reports back through Finished
        return Task.CompletedTask;
    }

    private async Task FinishMenuAsync(MenuStep step)
    {
        var open = _open!;
        _open = null;
        _hook.MenuMode = false;

        var reason = step.Chosen is not null ? MenuCloseReason.Chosen : _closeReason ?? MenuCloseReason.Escape;
        Record(new MenuClosed(Now, reason));

        if (step.Chosen is { } snippet && !_paused)
        {
            await ExpandContentAsync(open.Target, snippet, open.Match.Backspaces, string.Empty, fromMenu: true).ConfigureAwait(false);
        }
    }

    private async Task HandleForegroundChangedAsync()
    {
        CancelMenu(MenuCloseReason.FocusChanged);
        DisposePendingTimer();
        await RefreshCaptureUnlessSupersededAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Bursts of focus events (browsers fire several per click) are evaluated once: if another focus or foreground
    /// change is already queued, capture stays off and that one does the (UI Automation) evaluation.
    /// </summary>
    private Task RefreshCaptureUnlessSupersededAsync() =>
        _hook.Events.TryPeek(out var next) && next is FocusChanged { KnownField: false } or ForegroundChanged
            ? Task.CompletedTask
            : RefreshCaptureAsync();

    private async Task ApplyPauseAsync(bool paused)
    {
        if (paused)
        {
            CancelMenu(reason: null);
            DisposePendingTimer();
        }

        await RefreshCaptureAsync().ConfigureAwait(false);
        Record(new EngineStateChanged(Now, _paused ? EngineState.Paused : EngineState.Running));
    }

    /// <summary>Hides an open menu without inserting; <paramref name="reason"/> null skips the diagnostic.</summary>
    private void CancelMenu(MenuCloseReason? reason)
    {
        if (_open is null)
        {
            return; // leaving MenuMode untouched: turning it off clears the hook's pending trigger ("cp" before "cp1")
        }

        if (!_open.Shown)
        {
            DropUnshownMenu();
            return;
        }

        _hook.MenuMode = false;
        _open = null;
        _menu.Cancel();
        if (reason is { } closed)
        {
            Record(new MenuClosed(Now, closed));
        }
    }

    private void DisposeMenuDelayTimer()
    {
        _menuDelayTimer?.Dispose();
        _menuDelayTimer = null;
    }

    private void DisposePendingTimer()
    {
        _pendingTimer?.Dispose();
        _pendingTimer = null;
    }

    /// <summary>
    /// Capture stayed on for a field allowed before: confirm it without a gap. Only a clear "no" (it became a password
    /// field, say) turns capture off; no answer from UI Automation keeps the earlier verdict.
    /// </summary>
    private async Task RecheckKnownFieldAsync()
    {
        if (_paused)
        {
            return;
        }

        var target = await CaptureTargetAsync().ConfigureAwait(false);
        if (target is not null && !Volatile.Read(ref _policy).Evaluate(target, TextFlowFeature.Expansion).IsAllowed)
        {
            _hook.CaptureEnabled = false;
            _hook.ForgetKnownFields();
        }
    }

    private async Task RefreshCaptureAsync()
    {
        var focusVersion = _hook.FocusVersion;
        _hook.CaptureEnabled = false;
        if (_paused)
        {
            return;
        }

        var target = await CaptureTargetAsync().ConfigureAwait(false);
        if (target is not null && Volatile.Read(ref _policy).Evaluate(target, TextFlowFeature.Expansion).IsAllowed)
        {
            _hook.TryEnableCapture(focusVersion);
        }

        if (_paused)
        {
            _hook.CaptureEnabled = false; // Pause() ran while we evaluated
        }
    }

    /// <summary>Null when nothing is focused or UIA did not answer in time (the call keeps running in the background).</summary>
    /// <param name="locateCaret">Only menus and field prompts need the caret; the UI Automation lookup costs time.</param>
    private async Task<ActiveTarget?> CaptureTargetAsync(bool locateCaret = false)
    {
        try
        {
            return await Task.Run(() => _resolver.CaptureTarget(locateCaret), _stopping)
                .WaitAsync(_options.CaptureTimeout, _time, _stopping)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <param name="sendEnter">Enter follows the text; <c>{{cursor}}</c> is then ignored: the message is sent whole.</param>
    private async Task<InsertionResult> ExpandAsync(
        ActiveTarget target, string text, int backspaces, bool fromMenu, int caretOffset = 0, bool sendEnter = false)
    {
        // Chime the moment the text lands, not after the clipboard restore (~250 ms later): it must feel instant.
        var delivered = 0;
        var played = false;
        void OnDelivered()
        {
            if (Interlocked.Exchange(ref delivered, 1) == 0)
            {
                played = _feedback.Play();
            }
        }

        var request = new InsertionRequest(
            target, text, backspaces, CaretOffsetFromEnd: sendEnter ? 0 : caretOffset, Delivered: OnDelivered, PressEnterAfter: sendEnter);
        var result = await _insertion.InsertAsync(request, _stopping).ConfigureAwait(false);
        if (result.Succeeded)
        {
            OnDelivered(); // a strategy that does not report delivery
        }

        Record(new ExpansionCompleted(
            Now, target.ProcessName, result.Strategy, result.Status, result.Elapsed.TotalMilliseconds, fromMenu, played));
        return result;
    }

    /// <summary>Types back a key the hook swallowed for a trigger that did not expand.</summary>
    private async Task ReEmitAsync(ActiveTarget? target, char? swallowed)
    {
        if (target is null || swallowed is not { } key)
        {
            return;
        }

        var text = key == '\r' ? "\n" : key.ToString(); // SendInput turns \n into a real Enter key
        await _insertion.InsertAsync(
            new InsertionRequest(target, text, PreferredStrategy: InsertionStrategyKind.SendInput), _stopping).ConfigureAwait(false);
    }

    private void OnMenuFinished(int session, MenuStep step) => _inbox.Writer.TryWrite(new MenuFinished(session, step));

    private void Shutdown()
    {
        _menu.Finished -= OnMenuFinished;
        if (_fieldPrompt is not null)
        {
            _fieldPrompt.Finished -= OnFieldsFinished;
        }

        DisposePendingTimer();
        CancelMenu(reason: null);
        CancelFields(FieldsCloseReason.Replaced);
        _hook.CaptureEnabled = false;
        Record(new EngineStateChanged(Now, EngineState.Stopped));

        _inbox.Writer.TryComplete(); // later IdleAsync calls complete at once
        while (_inbox.Reader.TryRead(out var work))
        {
            if (work is Barrier barrier)
            {
                _barriers.Add(barrier);
            }
        }

        ReleaseBarriers();
    }

    private void Record(DiagnosticEvent diagnostic) => _sink.Record(diagnostic);

    private DateTimeOffset Now => _time.GetUtcNow();

    private sealed record OpenMenu(ActiveTarget Target, TriggerMatch Match, GroupMenu Menu, int Session, bool Shown = false);

    private abstract record EngineWork;

    private sealed record MenuFinished(int Session, MenuStep Step) : EngineWork;

    private sealed record PendingElapsed(int Version) : EngineWork;

    private sealed record MenuDelayElapsed(int Session) : EngineWork;

    private sealed record PaletteChosen(string SnippetId, ActiveTarget Target) : EngineWork;

    private sealed record PauseChanged(bool Paused) : EngineWork;

    private sealed record Barrier(TaskCompletionSource Done) : EngineWork;
}
