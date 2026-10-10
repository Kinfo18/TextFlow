using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Engine;
using TextFlow.Core.Input;

namespace TextFlow.Core.Tests.Engine;

/// <summary>Templates with fields (H5.2, ADR-0002 rev. 2026-10-04): ask, then insert in the original field.</summary>
public sealed partial class ExpansionEngineTests
{
    private static Dictionary<string, string> Values(params (string Name, string Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);

    [Fact]
    public async Task SnippetWithFields_RemovesTheTrigger_AndAsksForTheFields()
    {
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));

        var removal = Assert.Single(_insertion.Requests);
        Assert.Equal(string.Empty, removal.Text);
        Assert.Equal(2, removal.BackspacesBefore);
        Assert.Equal(InsertionStrategyKind.SendInput, removal.PreferredStrategy);
        Assert.Equal(["cliente"], _fields.Shown!.Select(f => f.Name));
        Assert.Equal(0, _feedback.Plays);
        Assert.Contains(_sink.Events, e => e is FieldsShown { FieldCount: 1, AnchoredToCaret: true });
    }

    [Fact]
    public async Task RepeatedField_IsAskedOnce_InOrderOfAppearance()
    {
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("frec1"), FakeResolver.Window));

        Assert.Equal(["cliente", "mes", "año", "monto"], _fields.Shown!.Select(f => f.Name));
    }

    [Fact]
    public async Task FilledFields_AreInsertedInTheOriginalField_AtOnce()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("frec1"), FakeResolver.Window));

        _fields.Finish(Values(("cliente", "Ana"), ("mes", "septiembre"), ("año", "2026"), ("monto", "$120")));
        await SettleAsync();

        Assert.Equal(1, _resolver.Activations);
        var insertion = _insertion.Requests[^1];
        Assert.Equal("Ana: septiembre/2026 $120 (Ana)", insertion.Text);
        Assert.Equal(0, insertion.BackspacesBefore);
        Assert.Equal(1, _feedback.Plays);
        Assert.Contains(_sink.Events, e => e is FieldsClosed { Reason: FieldsCloseReason.Inserted });
    }

    [Fact]
    public async Task CancelledFields_InsertNothing()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));

        _fields.Finish(null);
        await SettleAsync();

        Assert.Single(_insertion.Requests); // only the trigger removal
        Assert.Equal(0, _resolver.Activations);
        Assert.Contains(_sink.Events, e => e is FieldsClosed { Reason: FieldsCloseReason.Cancelled });
    }

    [Fact]
    public async Task WhenTheOriginalFieldIsNotFocused_ItWaitsForTheUser_InsteadOfTypingElsewhere()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        _resolver.Target = FakeResolver.Notepad with { FocusHandle = 0x9999 }; // another control has the focus now

        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();

        Assert.Single(_insertion.Requests);
        Assert.Equal(1, _fields.WaitingShown);
        Assert.Null(_fields.NotInsertedText);
    }

    [Fact]
    public async Task WhenTheUserReturnsToTheOriginalField_TheWaitingTextIsInserted()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        var original = _resolver.Target;
        _resolver.Target = FakeResolver.Notepad with { FocusHandle = 0x9999 };
        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();

        _resolver.Target = original;
        await TypeAsync(new FocusChanged(FakeResolver.Window));

        Assert.Equal("Hola, Ana ¡un gusto!", _insertion.Requests[^1].Text);
        Assert.Equal(1, _fields.Cancels); // the waiting message goes away
        Assert.Contains(_sink.Events, e => e is FieldsClosed { Reason: FieldsCloseReason.Inserted });
    }

    [Fact]
    public async Task WhileWaiting_FocusElsewhere_InsertsNothing()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        _resolver.Target = FakeResolver.Notepad with { FocusHandle = 0x9999 };
        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();

        await TypeAsync(new FocusChanged(FakeResolver.Window));
        await TypeAsync(new ForegroundChanged(0x4321));

        Assert.Single(_insertion.Requests);
    }

    [Fact]
    public async Task WhenTheUserNeverReturns_TheTextGoesToTheClipboard()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        _resolver.Target = FakeResolver.Notepad with { FocusHandle = 0x9999 };
        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();

        _time.Advance(ExpansionEngineOptions.Default.ReturnWaitTimeout);
        await SettleAsync();

        Assert.Single(_insertion.Requests);
        Assert.Equal("Hola, Ana ¡un gusto!", _fields.NotInsertedText);
        Assert.Contains(_sink.Events, e => e is FieldsClosed { Reason: FieldsCloseReason.TargetLost });
    }

    [Fact]
    public async Task CancellingWhileWaiting_InsertsNothing()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        var original = _resolver.Target;
        _resolver.Target = FakeResolver.Notepad with { FocusHandle = 0x9999 };
        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();

        _fields.Finish(null);
        await SettleAsync();
        _resolver.Target = original;
        await TypeAsync(new FocusChanged(FakeResolver.Window));

        Assert.Single(_insertion.Requests);
        Assert.Contains(_sink.Events, e => e is FieldsClosed { Reason: FieldsCloseReason.Cancelled });
    }

    private static ActiveTarget InField(string elementId) =>
        FakeResolver.Notepad with { Control = FakeResolver.Notepad.Control with { ElementId = elementId } };

    [Fact]
    public async Task WhenTheBrowserTabChanged_SameWindowButAnotherElement_ItWaits()
    {
        _resolver.Target = InField("tab-1");
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        _resolver.Target = InField("tab-2");

        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();

        Assert.Equal(1, _resolver.Restores); // tried to give the focus back; the field refused
        Assert.Single(_insertion.Requests);
        Assert.Equal(1, _fields.WaitingShown);
    }

    [Fact]
    public async Task FieldWithUiaIdentity_IsBookmarkedBeforeThePromptShows()
    {
        _resolver.Target = InField("chat-box");
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));

        Assert.Equal("chat-box", Assert.Single(_resolver.Bookmarks).Control.ElementId);
        Assert.NotNull(_fields.Shown);
    }

    [Fact]
    public async Task ValueCopiedFromTheSamePage_FocusGoesBackToTheField_AndTheTextIsInserted()
    {
        _resolver.Target = InField("chat-box");
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        _resolver.Target = InField("customer-name"); // the user clicked the name on the page to copy it
        _resolver.OnRestore = original =>
        {
            _resolver.Target = original;
            return true;
        };

        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();

        Assert.Equal(1, _resolver.Restores);
        Assert.Equal(0, _fields.WaitingShown);
        Assert.Equal("Hola, Ana ¡un gusto!", _insertion.Requests[^1].Text);
    }

    [Fact]
    public async Task RestoredFocus_ReportedAfterTheFirstWait_IsStillInserted()
    {
        _resolver.Target = InField("chat-box");
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        var original = _resolver.Target;
        _resolver.Target = InField("customer-name");
        var restoredAt = (long?)null;
        _resolver.OnRestore = _ =>
        {
            restoredAt = Environment.TickCount64;
            return true;
        };
        // Chrome reports the focus it was given only later, past FocusReturnTimeout (2026-10-10).
        var lag = ExpansionEngineOptions.Default.FocusReturnTimeout + TimeSpan.FromMilliseconds(200);
        _resolver.DuringCapture = () =>
        {
            if (restoredAt is { } at && Environment.TickCount64 - at >= lag.TotalMilliseconds)
            {
                _resolver.Target = original;
            }
        };

        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();

        Assert.Equal(0, _fields.WaitingShown);
        Assert.Equal("Hola, Ana ¡un gusto!", _insertion.Requests[^1].Text);
    }

    [Fact]
    public async Task RestoredFocusThatNeverShows_WaitsAndSaysSo()
    {
        _resolver.Target = InField("chat-box");
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        _resolver.Target = InField("customer-name");
        _resolver.OnRestore = _ => true;

        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();

        Assert.Equal(1, _fields.WaitingShown);
        Assert.Contains(_sink.Events, e => e is FieldsWaiting { FocusRestored: true });
    }

    [Fact]
    public async Task WhileWaiting_AClickInTheFieldThatAlreadyHasTheFocus_InsertsTheText()
    {
        _resolver.Target = InField("chat-box");
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        var original = _resolver.Target;
        _resolver.Target = InField("customer-name");
        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();
        Assert.Equal(1, _fields.WaitingShown);

        _resolver.Target = original; // focus is back without a focus event (TextFlow put it there)
        await TypeAsync(new PointerReleased());

        Assert.Equal("Hola, Ana ¡un gusto!", _insertion.Requests[^1].Text);
        Assert.Equal(1, _fields.Cancels);
        Assert.Contains(_sink.Events, e => e is FieldsClosed { Reason: FieldsCloseReason.Inserted });
    }

    [Fact]
    public async Task AClick_WithNothingWaiting_DoesNothing()
    {
        await StartAsync();
        var captures = _resolver.Captures;

        await TypeAsync(new PointerReleased());

        Assert.Equal(captures, _resolver.Captures);
        Assert.Empty(_insertion.Requests);
    }

    [Fact]
    public async Task WhenWindowsRefusesToReturnToTheWindow_ItWaits()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        _resolver.ActivateResult = false;
        _resolver.Target = FakeResolver.Notepad with { WindowHandle = 0x4321 }; // still on the data page

        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();

        Assert.Single(_insertion.Requests);
        Assert.Equal(1, _fields.WaitingShown);
    }

    [Fact]
    public async Task AStaleFieldsAnswer_IsIgnored()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));

        _fields.Finish(Values(("cliente", "Ana")), session: _fields.Session + 7);
        await SettleAsync();

        Assert.Single(_insertion.Requests);
    }

    [Fact]
    public async Task ANewTemplate_ReplacesTheOpenOne()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));

        await TypeAsync(new TriggerTyped(Match("frec1"), FakeResolver.Window));

        Assert.Equal(1, _fields.Cancels);
        Assert.Equal(4, _fields.Shown!.Count);
    }

    [Fact]
    public async Task SnippetWithOnlyVariables_ExpandsAtOnce_WithTheCaretAtTheCursorMarker()
    {
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("fecha"), FakeResolver.Window));

        var request = Assert.Single(_insertion.Requests);
        Assert.Equal($"Año {_time.GetLocalNow():yyyy} fin", request.Text);
        Assert.Equal(3, request.CaretOffsetFromEnd);
        Assert.Null(_fields.Shown);
    }
}
