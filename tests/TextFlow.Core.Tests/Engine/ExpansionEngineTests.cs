using Microsoft.Extensions.Time.Testing;
using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Engine;
using TextFlow.Core.Expansion;
using TextFlow.Core.Import;
using TextFlow.Core.Input;
using TextFlow.Core.Menus;
using TextFlow.Core.Security;
using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Engine;

public sealed class ExpansionEngineTests : IAsyncDisposable
{
    private readonly FakeHook _hook = new();
    private readonly FakeResolver _resolver = new();
    private readonly FakeInsertion _insertion = new();
    private readonly FakeMenu _menu = new();
    private readonly FakeFeedback _feedback = new();
    private readonly ListSink _sink = new();
    private readonly FakeTimeProvider _time = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ExpansionEngine _engine;
    private readonly LibraryIndex _index;
    private Task? _run;

    public ExpansionEngineTests()
    {
        var root = new LibraryGroup("root", "root", null, true,
            [
                new LibraryGroup("g-lc", "Local cerrado", "LC", true, [], [Snippet("s-nc", "No confirmado", "texto nc")]),
                new LibraryGroup("g-cp", "Completa pasos", "cp", true, [], [Snippet("s-cp1", "cp1", "texto cp1")]),
                new LibraryGroup("g-t1", "Temples", null, true, [], [Snippet("s-cc", "cc", "texto cc"), Snippet("s-dir1", "dir1", "d1"), Snippet("s-dir12", "dir12", "d12")]),
            ],
            []);
        _index = LibraryIndex.Build(root);
        _engine = new ExpansionEngine(
            _hook, _resolver, new SecurityPolicy(BuiltinExclusions.All), _insertion, _menu, _feedback, new FakePointer(),
            _sink, _time, ExpansionEngineOptions.Default);
    }

    private static LibrarySnippet Snippet(string id, string abbreviation, string content) =>
        new(id, abbreviation, content, IsRichText: false, [abbreviation]);

    private string IdOf(string trigger) => _index.Triggers.Single(t => t.Trigger == trigger).SnippetId;

    private TriggerMatch Match(string trigger, char? delimiter = null) => new(IdOf(trigger), trigger, delimiter, trigger.Length);

    private async Task StartAsync()
    {
        await _engine.LoadAsync(_index);
        _run = _engine.RunAsync(_cts.Token);
        await SettleAsync();
    }

    /// <summary>Lets the engine loop drain everything queued so far.</summary>
    private async Task SettleAsync()
    {
        await _engine.IdleAsync();
    }

    private async Task TypeAsync(HookEvent evt)
    {
        _hook.Raise(evt);
        await SettleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_run is not null)
        {
            await _run;
        }

        _cts.Dispose();
    }

    [Fact]
    public async Task Load_PushesLibraryTriggersToTheHook()
    {
        await StartAsync();

        Assert.Equal(_index.Triggers.Count, _hook.Triggers.Count);
    }

    [Fact]
    public async Task Start_EnablesCapture_WhenForegroundIsAllowed()
    {
        await StartAsync();

        Assert.True(_hook.CaptureEnabled);
        Assert.Contains(_sink.Events, e => e is EngineStateChanged { State: EngineState.Running });
    }

    [Fact]
    public async Task SnippetTrigger_InsertsContentReplacingTheTrigger_AndPlaysSound()
    {
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("cc"), FakeResolver.Window));

        var request = Assert.Single(_insertion.Requests);
        Assert.Equal("texto cc", request.Text);
        Assert.Equal(2, request.BackspacesBefore);
        Assert.Equal(1, _feedback.Plays);
        Assert.Contains(_sink.Events, e => e is ExpansionCompleted { Status: InsertionStatus.Success, FromMenu: false, SoundPlayed: true });
    }

    [Fact]
    public async Task FailedInsertion_DoesNotPlaySound()
    {
        await StartAsync();
        _insertion.NextStatus = InsertionStatus.TargetChanged;

        await TypeAsync(new TriggerTyped(Match("cc"), FakeResolver.Window));

        Assert.Equal(0, _feedback.Plays);
    }

    [Fact]
    public async Task Sound_PlaysAsSoonAsTheTextIsDelivered_NotAfterTheClipboardIsRestored()
    {
        await StartAsync();
        var restore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _insertion.HoldAfterDelivery = restore;

        _hook.Raise(new TriggerTyped(Match("cc"), FakeResolver.Window));
        await _insertion.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, _feedback.Plays); // insertion still running (restoring the clipboard)

        restore.SetResult();
        await SettleAsync();
        Assert.Equal(1, _feedback.Plays);
        Assert.Contains(_sink.Events, e => e is ExpansionCompleted { SoundPlayed: true });
    }

    [Fact]
    public async Task Sound_AfterDelivery_StaysPlayedEvenIfTheClipboardCouldNotBeRestored()
    {
        await StartAsync();
        var restore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _insertion.HoldAfterDelivery = restore;
        _insertion.NextStatus = InsertionStatus.ClipboardConflict;

        _hook.Raise(new TriggerTyped(Match("cc"), FakeResolver.Window));
        await _insertion.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        restore.SetResult();
        await SettleAsync();

        Assert.Equal(1, _feedback.Plays);
        Assert.Contains(_sink.Events, e => e is ExpansionCompleted { Status: InsertionStatus.ClipboardConflict, SoundPlayed: true });
    }

    [Fact]
    public async Task MenuKeys_WarmTheAudioDevice_SoAChoiceAfterALongLookIsHeard()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));
        var before = _feedback.Warms;

        await TypeAsync(new MenuKeyPressed(MenuInput.Down));

        Assert.Equal(before + 1, _feedback.Warms);
    }

    [Fact]
    public async Task MenuTrigger_ShowsMenuAtCaret_AndEntersMenuMode()
    {
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        Assert.Equal("LC", _menu.Shown?.Trigger);
        Assert.Equal(FakeResolver.Notepad.Control.CaretBounds, _menu.Anchor);
        Assert.True(_hook.MenuMode);
        Assert.Contains(_sink.Events, e => e is MenuShown { AnchoredToCaret: true });
    }

    [Fact]
    public async Task MenuWithoutCaret_AnchorsAtPointer()
    {
        _resolver.Target = FakeResolver.Notepad with { Control = FakeResolver.Notepad.Control with { CaretBounds = null } };
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        Assert.Equal(new FakePointer().CursorAnchor(), _menu.Anchor);
    }

    [Fact]
    public async Task MenuKeys_AreForwardedToTheMenu()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        await TypeAsync(new MenuKeyPressed(MenuInput.Down));

        Assert.Equal([MenuInput.Down], _menu.Inputs);
    }

    [Fact]
    public async Task ChoosingFromMenu_InsertsSnippet_ReplacingTheGroupAbbreviation()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        _menu.Finish(new MenuStep(MenuNavigator.Open(_menu.Shown!), Chosen: new MenuSnippetEntry("No confirmado", "texto nc")));
        await SettleAsync();

        var request = Assert.Single(_insertion.Requests);
        Assert.Equal("texto nc", request.Text);
        Assert.Equal(2, request.BackspacesBefore);
        Assert.False(_hook.MenuMode);
        Assert.Contains(_sink.Events, e => e is MenuClosed { Reason: MenuCloseReason.Chosen });
        Assert.Contains(_sink.Events, e => e is ExpansionCompleted { FromMenu: true });
    }

    [Fact]
    public async Task EscapeFromMenu_InsertsNothing()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        _menu.Finish(new MenuStep(MenuNavigator.Open(_menu.Shown!), Closed: true));
        await SettleAsync();

        Assert.Empty(_insertion.Requests);
        Assert.False(_hook.MenuMode);
        Assert.Contains(_sink.Events, e => e is MenuClosed { Reason: MenuCloseReason.Escape });
    }

    [Fact]
    public async Task PendingMenuTrigger_OpensMenuImmediately()
    {
        await StartAsync();

        await TypeAsync(new TriggerPending(Match("cp"), 7, FakeResolver.Window));

        Assert.Equal("cp", _menu.Shown?.Trigger);
        Assert.Empty(_hook.Flushed); // no timeout for menus
    }

    [Fact]
    public async Task PendingMenuTrigger_KeepsTheHookPendingTrigger_SoDigitsCanContinueIt()
    {
        await StartAsync();
        var clearedBefore = _hook.PendingCleared;

        await TypeAsync(new TriggerPending(Match("cp"), 7, FakeResolver.Window));

        Assert.True(_hook.MenuMode);
        Assert.Equal(clearedBefore, _hook.PendingCleared); // "cp" must stay pending for "cp1"
    }

    [Fact]
    public async Task LongerTriggerWhileMenuOpen_CancelsMenu_AndExpandsLongerOne()
    {
        await StartAsync();
        await TypeAsync(new TriggerPending(Match("cp"), 7, FakeResolver.Window));

        await TypeAsync(new TriggerTyped(Match("cp1"), FakeResolver.Window));

        Assert.Equal(1, _menu.Cancels);
        Assert.False(_hook.MenuMode);
        Assert.Equal("texto cp1", Assert.Single(_insertion.Requests).Text);
        Assert.Contains(_sink.Events, e => e is MenuClosed { Reason: MenuCloseReason.LongerTrigger });
    }

    [Fact]
    public async Task PendingSnippetTrigger_IsFlushedAfterTimeout()
    {
        await StartAsync();
        await TypeAsync(new TriggerPending(Match("dir1"), 3, FakeResolver.Window));
        Assert.Empty(_hook.Flushed);

        _time.Advance(ExpansionEngineOptions.Default.PendingTimeout);
        await SettleAsync();

        Assert.Equal([3], _hook.Flushed);
    }

    [Fact]
    public async Task PendingMenuBrokenByAnotherKey_ReEmitsThatKey_WithoutMenu()
    {
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("cp", delimiter: 'x'), FakeResolver.Window));

        Assert.Null(_menu.Shown);
        var request = Assert.Single(_insertion.Requests);
        Assert.Equal("x", request.Text);
        Assert.Equal(0, request.BackspacesBefore);
        Assert.Equal(InsertionStrategyKind.SendInput, request.PreferredStrategy);
    }

    [Fact]
    public async Task SnippetFiredByBreakingKey_TypesThatKeyAfterTheExpansion()
    {
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("dir1", delimiter: ' '), FakeResolver.Window));

        Assert.Equal("d1 ", Assert.Single(_insertion.Requests).Text);
    }

    [Fact]
    public async Task TriggerFromAnotherWindow_IsRejected_WithoutTypingIntoTheNewWindow()
    {
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("cc", delimiter: ' '), ForegroundWindow: 0x9999));

        Assert.Empty(_insertion.Requests);
        Assert.Contains(_sink.Events, e => e is TargetRejected { Reason: RejectionReason.WindowChanged });
    }

    [Fact]
    public async Task PolicyRejection_ReEmitsTheSwallowedKey()
    {
        await StartAsync();
        _resolver.Target = FakeResolver.Notepad with { ProcessName = "WindowsTerminal.exe" };

        await TypeAsync(new TriggerTyped(Match("cc", delimiter: ' '), FakeResolver.Window));

        Assert.Equal(" ", Assert.Single(_insertion.Requests).Text);
        Assert.Contains(_sink.Events, e => e is TargetRejected { Reason: RejectionReason.PolicyDenied });
    }

    [Fact]
    public async Task FailedExpansion_ReEmitsTheBreakingKey()
    {
        await StartAsync();
        _insertion.NextStatus = InsertionStatus.ClipboardConflict;

        await TypeAsync(new TriggerTyped(Match("dir1", delimiter: ' '), FakeResolver.Window));

        Assert.Equal(["d1 ", " "], _insertion.Requests.Select(r => r.Text));
    }

    [Fact]
    public async Task StaleMenuResult_DoesNotCloseTheNewerMenu()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));
        var first = _menu.Session;
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        _menu.Finish(new MenuStep(MenuNavigator.Open(_menu.Shown!), Chosen: new MenuSnippetEntry("No confirmado", "texto nc")), first);
        await SettleAsync();

        Assert.Empty(_insertion.Requests);
        Assert.True(_hook.MenuMode);
    }

    [Fact]
    public async Task TriggerQueuedBeforePause_DoesNotExpand()
    {
        await StartAsync();

        _engine.Pause();
        await TypeAsync(new TriggerTyped(Match("cc"), FakeResolver.Window));
        await TypeAsync(new TriggerPending(Match("cp"), 1, FakeResolver.Window));

        Assert.Empty(_insertion.Requests);
        Assert.Null(_menu.Shown);
    }

    [Fact]
    public async Task ConcurrentIdleWaits_AllComplete()
    {
        await StartAsync();
        _hook.Raise(new TriggerTyped(Match("cc"), FakeResolver.Window));

        await Task.WhenAll(_engine.IdleAsync(), _engine.IdleAsync(), _engine.IdleAsync()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(_insertion.Requests);
    }

    [Fact]
    public async Task HookClosed_StopsTheEngine_FailClosed()
    {
        await StartAsync();

        _hook.Close();
        await _run!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(_hook.CaptureEnabled);
        Assert.Contains(_sink.Events, e => e is EngineFault);
        Assert.Contains(_sink.Events, e => e is EngineStateChanged { State: EngineState.Stopped });
    }

    [Fact]
    public async Task PasswordField_IsRejected()
    {
        await StartAsync();
        _resolver.Target = FakeResolver.Notepad with { Control = FakeResolver.Notepad.Control with { IsPassword = true } };

        await TypeAsync(new TriggerTyped(Match("cc"), FakeResolver.Window));

        Assert.Empty(_insertion.Requests);
    }

    [Fact]
    public async Task ForegroundChangeWithMenuOpen_CancelsMenu()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        await TypeAsync(new ForegroundChanged(0x4321));

        Assert.Equal(1, _menu.Cancels);
        Assert.False(_hook.MenuMode);
        Assert.Contains(_sink.Events, e => e is MenuClosed { Reason: MenuCloseReason.FocusChanged });
    }

    [Fact]
    public async Task ClickInsideMenu_KeepsItOpen_ClickOutsideDismisses()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        _menu.ContainsResult = true;
        await TypeAsync(new MenuInterrupted(10, 10));
        Assert.Equal(0, _menu.Dismissals);

        _menu.ContainsResult = false;
        await TypeAsync(new MenuInterrupted(10, 10));
        Assert.Equal(1, _menu.Dismissals);
        Assert.Contains(_sink.Events, e => e is MenuClosed { Reason: MenuCloseReason.ClickOutside });
    }

    [Fact]
    public async Task OtherKeyWithMenuOpen_Dismisses()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        await TypeAsync(new MenuInterrupted());

        Assert.Equal(1, _menu.Dismissals);
        Assert.Contains(_sink.Events, e => e is MenuClosed { Reason: MenuCloseReason.OtherKey });
    }

    [Fact]
    public async Task Pause_DisablesCapture_EvenAfterForegroundChanges_UntilResume()
    {
        await StartAsync();

        _engine.Pause();
        await TypeAsync(new ForegroundChanged(FakeResolver.Window));
        Assert.False(_hook.CaptureEnabled);
        Assert.True(_engine.IsPaused);

        _engine.Resume();
        await SettleAsync();
        Assert.True(_hook.CaptureEnabled);
        Assert.Contains(_sink.Events, e => e is EngineStateChanged { State: EngineState.Paused });
    }

    [Fact]
    public async Task ExcludedForeground_DisablesCapture()
    {
        await StartAsync();
        _resolver.Target = FakeResolver.Notepad with { ProcessName = "WindowsTerminal.exe" };

        await TypeAsync(new ForegroundChanged(FakeResolver.Window));

        Assert.False(_hook.CaptureEnabled);
    }

    [Fact]
    public async Task TypingActivity_WarmsTheAudioDevice_SoTheFirstChimeAfterIdleIsHeard()
    {
        await StartAsync();

        await TypeAsync(new TypingActivity());

        Assert.Equal(1, _feedback.Warms);
        Assert.Equal(0, _feedback.Plays);
    }

    [Fact]
    public async Task FocusMovesToAPasswordField_CaptureStopsUntilFocusLeavesIt()
    {
        await StartAsync();
        Assert.True(_hook.CaptureEnabled);

        _resolver.Target = FakeResolver.Notepad with { Control = FakeResolver.Notepad.Control with { IsPassword = true } };
        _hook.RaiseFocusChange(new FocusChanged(FakeResolver.Window));
        await SettleAsync();
        Assert.False(_hook.CaptureEnabled);

        _resolver.Target = FakeResolver.Notepad;
        _hook.RaiseFocusChange(new FocusChanged(FakeResolver.Window));
        await SettleAsync();
        Assert.True(_hook.CaptureEnabled);
    }

    [Fact]
    public async Task FocusChangingWhileEvaluating_LeavesCaptureOff_ForTheNextEvaluation()
    {
        await StartAsync();
        _resolver.DuringCapture = () =>
        {
            _resolver.DuringCapture = null;
            _hook.FocusVersion++; // the user tabbed into the password field meanwhile (event still to come)
        };

        _hook.RaiseFocusChange(new FocusChanged(FakeResolver.Window));
        await SettleAsync();

        Assert.False(_hook.CaptureEnabled); // a stale "allowed" must never re-enable capture
    }

    [Fact]
    public async Task BurstOfFocusChanges_IsEvaluatedOnce()
    {
        await StartAsync();
        var before = _resolver.Captures;

        _hook.RaiseFocusChange(new FocusChanged(FakeResolver.Window));
        _hook.RaiseFocusChange(new FocusChanged(FakeResolver.Window));
        _hook.RaiseFocusChange(new FocusChanged(FakeResolver.Window));
        await SettleAsync();

        Assert.Equal(1, _resolver.Captures - before);
        Assert.True(_hook.CaptureEnabled);
    }

    [Fact]
    public async Task FocusChange_DoesNotCloseAnOpenMenu()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        await TypeAsync(new FocusChanged(FakeResolver.Window));

        Assert.Equal(0, _menu.Cancels);
        Assert.True(_hook.MenuMode);
    }

    [Fact]
    public async Task Diagnostics_NeverContainSnippetContent()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("cc"), FakeResolver.Window));

        var dump = string.Join("\n", _sink.Events.Select(e => e.ToString()));

        Assert.DoesNotContain("texto cc", dump, StringComparison.Ordinal);
    }
}
