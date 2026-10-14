using System.Globalization;
using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Input;
using TextFlow.Core.Menus;
using TextFlow.Core.Templates;

namespace TextFlow.Core.Engine;

/// <summary>
/// Templates (H5.2, ADR-0002 rev. 2026-10-04). Variables and <c>{{cursor}}</c> expand at once; a template with fields
/// removes its trigger, asks for the values in <see cref="IFieldPrompt"/> (the user may go and copy them from another
/// window) and then returns to the original field to insert the final text in one go. If that field cannot be reached
/// again the text is offered to the user instead of being typed somewhere else.
/// </summary>
public sealed partial class ExpansionEngine
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    private OpenFields? _openFields;
    private Waiting? _waiting;
    private int _fieldsSession;

    /// <summary>Expands snippet content: plain text as is, templates rendered or, with fields, asked for first.</summary>
    /// <returns>Null when the fields prompt took over (nothing inserted yet).</returns>
    /// <param name="selection">Text selected when the palette opened, for {{selection}}; null for typed triggers.</param>
    private async Task<InsertionResult?> ExpandContentAsync(
        ActiveTarget target, MenuSnippetEntry snippet, int backspaces, string trailing, bool fromMenu, string? selection = null)
    {
        var content = snippet.Content;
        if (!content.Contains("{{", StringComparison.Ordinal))
        {
            return CountUse(snippet, await ExpandAsync(target, content + trailing, backspaces, fromMenu, sendEnter: snippet.SendEnter)
                .ConfigureAwait(false)); // fast path
        }

        var template = TemplateParser.Parse(content);
        if (template.RequiresInput && _fieldPrompt is not null)
        {
            CountUse(snippet); // its form opened: the user reached for this snippet, whatever happens to the fields
            await AskFieldsAsync(target, template, backspaces, trailing, fromMenu, snippet.SendEnter, selection).ConfigureAwait(false);
            return null;
        }

        var rendered = Render(template, NoValues, new SelectedText(_variables, selection));
        return CountUse(snippet, await ExpandAsync(target, rendered.Text + trailing, backspaces, fromMenu, CaretOffset(rendered, trailing), snippet.SendEnter)
            .ConfigureAwait(false));
    }

    private InsertionResult CountUse(MenuSnippetEntry snippet, InsertionResult result)
    {
        if (result.Succeeded)
        {
            CountUse(snippet);
        }

        return result;
    }

    private void CountUse(MenuSnippetEntry snippet)
    {
        if (snippet.Id is { } id)
        {
            _usage?.Record(id);
        }
    }

    private async Task AskFieldsAsync(
        ActiveTarget target, ParsedTemplate template, int backspaces, string trailing, bool fromMenu, bool sendEnter, string? selection)
    {
        CancelFields(FieldsCloseReason.Replaced);

        if (backspaces > 0)
        {
            // The trigger goes now: the user sees the template was recognised, and nothing is left to delete later.
            var removal = await _insertion.InsertAsync(
                new InsertionRequest(target, string.Empty, backspaces, PreferredStrategy: InsertionStrategyKind.SendInput),
                _stopping).ConfigureAwait(false);
            if (!removal.Succeeded)
            {
                return;
            }
        }

        if (target.Control.ElementId is not null)
        {
            // Before the prompt takes the focus, and once the target has had a moment to delete the trigger.
            await Task.Delay(BookmarkSettle, TimeProvider.System, _stopping).ConfigureAwait(false);
            await Task.Run(() => _resolver.BookmarkField(target), _stopping).ConfigureAwait(false);
        }

        var caret = target.Control.CaretBounds;
        // Filling the fields means copying, so {{clipboard}} keeps what was copied when the trigger fired.
        IVariableSource live = new SelectedText(_variables, selection);
        var variables = template.UsesClipboard ? new FrozenClipboard(live, live.GetClipboardText()) : live;
        var open = new OpenFields(target, template, variables, trailing, fromMenu, sendEnter, ++_fieldsSession);
        _openFields = open;
        _fieldPrompt!.Show(template.Fields, caret ?? _pointer.CursorAnchor(), target.Monitor, open.Session);
        Record(new FieldsShown(Now, target.ProcessName, template.Fields.Count, AnchoredToCaret: caret is not null));
    }

    private async Task FinishFieldsAsync(FieldsFinished finished)
    {
        if (_waiting is { } waiting && waiting.Session == finished.Session && finished.Values is null)
        {
            StopWaiting();
            Record(new FieldsClosed(Now, FieldsCloseReason.Cancelled)); // "Cancelar" on the waiting message
            return;
        }

        if (_openFields is not { } open || open.Session != finished.Session)
        {
            return; // an answer for a prompt that was already replaced
        }

        _openFields = null;
        if (finished.Values is null)
        {
            Record(new FieldsClosed(Now, FieldsCloseReason.Cancelled));
            return;
        }

        var rendered = Render(open.Template, finished.Values, open.Variables);
        var text = rendered.Text + open.Trailing;
        var caretOffset = CaretOffset(rendered, open.Trailing);
        if (_paused)
        {
            OfferText(text);
            return;
        }

        var (target, restored, miss) = await ReturnToAsync(open.Target).ConfigureAwait(false);
        if (target is null)
        {
            // Still on the page the values came from (another tab cannot be switched to from here): wait for the user.
            var timer = _time.CreateTimer(
                _ => _inbox.Writer.TryWrite(new WaitElapsed(open.Session)), null, _options.ReturnWaitTimeout, Timeout.InfiniteTimeSpan);
            _waiting = new Waiting(open.Target, text, caretOffset, open.FromMenu, open.SendEnter, open.Session, timer);
            _fieldPrompt!.ShowWaiting();
            Record(new FieldsWaiting(Now, FocusRestored: restored, miss));
            return;
        }

        await DeliverAsync(target, text, caretOffset, open.FromMenu, open.SendEnter).ConfigureAwait(false);
    }

    private async Task DeliverAsync(ActiveTarget target, string text, int caretOffset, bool fromMenu, bool sendEnter)
    {
        var result = await ExpandAsync(target, text, 0, fromMenu, caretOffset, sendEnter).ConfigureAwait(false);
        if (result is { Succeeded: true } or { InputSent: true })
        {
            Record(new FieldsClosed(Now, FieldsCloseReason.Inserted));
            return;
        }

        OfferText(text);
    }

    private void OfferText(string text)
    {
        _fieldPrompt!.ShowNotInserted(text);
        Record(new FieldsClosed(Now, FieldsCloseReason.TargetLost));
    }

    /// <summary>While filled-in fields wait, every focus move checks whether the user is back in the original field.</summary>
    private async Task FocusMovedWhileWaitingAsync(HookEvent moved)
    {
        await DeliverIfBackAsync(clicked: false).ConfigureAwait(false);

        if (moved is ForegroundChanged)
        {
            await HandleForegroundChangedAsync().ConfigureAwait(false);
        }
        else if (_open is null)
        {
            await RefreshCaptureUnlessSupersededAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inserts the waiting text if the user is back in the original field. Runs on focus moves and on clicks: a click
    /// into a field that already has the focus (TextFlow put it back there) raises no focus event.
    /// </summary>
    /// <param name="clicked">
    /// The user clicked: the page may report the new focus a moment later, so a click in the original window is
    /// checked for a short while; and a click in a text box of that window counts as the way back, even if the page
    /// rebuilt the box since the abbreviation was typed (2026-10-11: the text waited although the user clicked the chat).
    /// </param>
    private async Task DeliverIfBackAsync(bool clicked)
    {
        if (_waiting is not { } waiting || _paused)
        {
            return;
        }

        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            var current = await CaptureFieldAsync().ConfigureAwait(false);
            if (_waiting != waiting)
            {
                return; // cancelled, given up or replaced meanwhile
            }

            if (IsSameField(waiting.Target, current) || (clicked && IsTextBoxOfSameWindow(waiting.Target, current)))
            {
                StopWaiting();
                _fieldPrompt!.Cancel(); // the "go back" message has done its job
                await DeliverAsync(current!, waiting.Text, waiting.CaretOffset, waiting.FromMenu, waiting.SendEnter).ConfigureAwait(false);
                return;
            }

            var miss = MissOf(waiting.Target, current);
            if (!clicked || miss is FieldReturnMiss.NoTarget or FieldReturnMiss.OtherWindow
                || TimeProvider.System.GetElapsedTime(started) >= ClickRecheckTimeout)
            {
                if (clicked)
                {
                    Record(new FieldsClickMissed(Now, miss));
                }

                return;
            }

            // Real time on purpose, as in ReturnToAsync: the page moves its focus on its own schedule.
            await Task.Delay(FocusReturnPoll, TimeProvider.System, _stopping).ConfigureAwait(false);
        }
    }

    private Task GiveUpWaitingAsync(WaitElapsed elapsed)
    {
        if (_waiting is { } waiting && waiting.Session == elapsed.Session)
        {
            StopWaiting();
            OfferText(waiting.Text);
        }

        return Task.CompletedTask;
    }

    private void StopWaiting()
    {
        _waiting?.Timer.Dispose();
        _waiting = null;
    }

    /// <summary>Brings the original window back and waits until its original field has the focus again.</summary>
    /// <returns>
    /// The target once it is back (null if not), whether TextFlow had put the focus back itself, and what the last
    /// check saw instead.
    /// </returns>
    private async Task<(ActiveTarget? Target, bool Restored, FieldReturnMiss Miss)> ReturnToAsync(ActiveTarget original)
    {
        if (!_resolver.Activate(original))
        {
            return (null, false, FieldReturnMiss.OtherWindow);
        }

        var started = TimeProvider.System.GetTimestamp();
        var timeout = _options.FocusReturnTimeout;
        var tried = false;
        var restored = false;
        while (true)
        {
            var current = await CaptureFieldAsync().ConfigureAwait(false);
            if (IsSameField(original, current))
            {
                return (current, restored, FieldReturnMiss.None);
            }

            if (!tried && IsSameWindowOtherField(original, current))
            {
                // The value was copied from the same page (2026-10-08): put the focus back instead of waiting for a click.
                tried = true;
                restored = await Task.Run(() => _resolver.RestoreField(original), _stopping).ConfigureAwait(false);
                if (restored)
                {
                    // A fresh wait: the browser reports the restored focus on its own schedule, and the first 400 ms
                    // are often gone by now (2026-10-10: the text waited for a click on a field that had the focus).
                    started = TimeProvider.System.GetTimestamp();
                    timeout = RestoredFocusTimeout;
                }

                continue;
            }

            if (TimeProvider.System.GetElapsedTime(started) >= timeout)
            {
                var miss = MissOf(original, current);
                // TextFlow focused that very field and nothing says otherwise: the page only could not tell which
                // element has the focus in time (a busy page), so the text goes in instead of waiting for a click.
                return restored && miss == FieldReturnMiss.ElementUnknown ? (current, restored, miss) : (null, restored, miss);
            }

            // Real time on purpose: Windows moves the focus back on its own schedule, not the engine's clock.
            await Task.Delay(FocusReturnPoll, TimeProvider.System, _stopping).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan FocusReturnPoll = TimeSpan.FromMilliseconds(40);

    private static readonly TimeSpan RestoredFocusTimeout = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan ClickRecheckTimeout = TimeSpan.FromMilliseconds(600);

    /// <summary>The patient field inspector answers within about 0.8 s; past this the target counts as missing.</summary>
    private static readonly TimeSpan PatientCaptureTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan BookmarkSettle = TimeSpan.FromMilliseconds(60);

    private static bool IsSameWindowOtherField(ActiveTarget original, ActiveTarget? current) =>
        current is not null
        && current.WindowHandle == original.WindowHandle
        && current.ProcessId == original.ProcessId
        && original.Control.ElementId is not null;

    /// <summary>The focused control as the field checks see it: no caret lookup, more time for UI Automation.</summary>
    private async Task<ActiveTarget?> CaptureFieldAsync()
    {
        try
        {
            return await Task.Run(_resolver.CaptureTargetPatiently, _stopping)
                .WaitAsync(PatientCaptureTimeout, _time, _stopping)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// A writable, non-password text box in the original window (web chat boxes are "Edit"). Any control of that
    /// window: Chrome's Win32 focus handle can stay on another control after a click in the page (2026-10-12).
    /// </summary>
    private static bool IsTextBoxOfSameWindow(ActiveTarget original, ActiveTarget? current) =>
        MissOf(original, current) is FieldReturnMiss.OtherElement or FieldReturnMiss.OtherControl
        && current!.Control is { ControlType: "Edit", IsPassword: false, IsReadOnly: false };

    private static FieldReturnMiss MissOf(ActiveTarget original, ActiveTarget? current) => current switch
    {
        null => FieldReturnMiss.NoTarget,
        _ when current.WindowHandle != original.WindowHandle || current.ProcessId != original.ProcessId => FieldReturnMiss.OtherWindow,
        // UI Automation naming the very element wins over the Win32 focus handle, which browsers update late.
        _ when original.Control.ElementId is { } id && current.Control.ElementId == id => FieldReturnMiss.None,
        _ when original.FocusHandle != 0 && current.FocusHandle != original.FocusHandle => FieldReturnMiss.OtherControl,
        _ when original.Control.ElementId is null || current.Control.ElementId == original.Control.ElementId => FieldReturnMiss.None,
        _ when current.Control.ElementId is null => FieldReturnMiss.ElementUnknown,
        _ => FieldReturnMiss.OtherElement,
    };

    /// <summary>Same window, control and (when known) UI Automation element: browser tabs share one window.</summary>
    private static bool IsSameField(ActiveTarget original, ActiveTarget? current) =>
        MissOf(original, current) == FieldReturnMiss.None;

    /// <summary>Hides an open prompt without inserting (another template, pause, shutdown).</summary>
    private void CancelFields(FieldsCloseReason reason)
    {
        if (_openFields is null && _waiting is null)
        {
            return;
        }

        _openFields = null;
        StopWaiting();
        _fieldPrompt?.Cancel();
        Record(new FieldsClosed(Now, reason));
    }

    private RenderedTemplate Render(
        ParsedTemplate template, IReadOnlyDictionary<string, string> values, IVariableSource? variables = null) =>
        TemplateRenderer.Render(template, values, variables ?? _variables, CultureInfo.CurrentCulture);

    /// <summary><c>{{cursor}}</c> counts from the end of the text, so a trailing key typed after it moves it too.</summary>
    private static int CaretOffset(RenderedTemplate rendered, string trailing) =>
        rendered.CaretOffsetFromEnd == 0 ? 0 : rendered.CaretOffsetFromEnd + trailing.Length;

    private void OnFieldsFinished(int session, IReadOnlyDictionary<string, string>? values) =>
        _inbox.Writer.TryWrite(new FieldsFinished(session, values));

    private sealed record OpenFields(
        ActiveTarget Target, ParsedTemplate Template, IVariableSource Variables, string Trailing, bool FromMenu, bool SendEnter, int Session);

    private sealed record FieldsFinished(int Session, IReadOnlyDictionary<string, string>? Values) : EngineWork;

    private sealed record Waiting(
        ActiveTarget Target, string Text, int CaretOffset, bool FromMenu, bool SendEnter, int Session, ITimer Timer);

    private sealed record WaitElapsed(int Session) : EngineWork;

    /// <summary>The clipboard as it was when the fields prompt opened; the rest stays live. Lives only while the prompt is open.</summary>
    private sealed class FrozenClipboard(IVariableSource live, string? clipboard) : IVariableSource
    {
        public DateTimeOffset Now => live.Now;

        public string? GetClipboardText() => clipboard;

        public string? GetSelectionText() => live.GetSelectionText();
    }

    /// <summary>The live variables plus what the palette saw selected; the host's own source never knows the selection.</summary>
    private sealed class SelectedText(IVariableSource live, string? selection) : IVariableSource
    {
        public DateTimeOffset Now => live.Now;

        public string? GetClipboardText() => live.GetClipboardText();

        public string? GetSelectionText() => selection;
    }

    /// <summary>Date and time only: used when the host gives no variable source.</summary>
    private sealed class ClockVariables(TimeProvider time) : IVariableSource
    {
        public DateTimeOffset Now => time.GetLocalNow();

        public string? GetClipboardText() => null;

        public string? GetSelectionText() => null;
    }
}
