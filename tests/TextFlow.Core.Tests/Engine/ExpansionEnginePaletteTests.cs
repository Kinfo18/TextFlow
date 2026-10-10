using TextFlow.Core.Diagnostics;

namespace TextFlow.Core.Tests.Engine;

/// <summary>
/// Command palette (D11): the user picks a snippet by name in a window that took the focus; the engine puts the focus
/// back on the field captured before the palette opened and inserts there, with the same checks as a typed trigger.
/// </summary>
public sealed partial class ExpansionEngineTests
{
    [Fact]
    public async Task PaletteTarget_IsCapturedAndBookmarked_BeforeThePaletteTakesTheFocus()
    {
        await StartAsync();

        var target = await _engine.CapturePaletteTargetAsync();

        Assert.Equal(FakeResolver.Notepad, target);
        Assert.Equal([FakeResolver.Notepad], _resolver.Bookmarks);
    }

    [Fact]
    public async Task PaletteChoice_ReturnsToTheField_AndInsertsWithNothingToDelete()
    {
        await StartAsync();

        _engine.InsertFromPalette("s-cc", FakeResolver.Notepad);
        await SettleAsync();

        Assert.Equal(1, _resolver.Activations);
        var request = Assert.Single(_insertion.Requests);
        Assert.Equal("texto cc", request.Text);
        Assert.Equal(0, request.BackspacesBefore);
        Assert.Equal(["s-cc"], _usage.Recorded);
    }

    [Fact]
    public async Task PaletteChoice_WorksForMenuOnlySnippets()
    {
        await StartAsync();

        _engine.InsertFromPalette("s-nc", FakeResolver.Notepad); // lives only in the LC menu
        await SettleAsync();

        Assert.Equal("texto nc", Assert.Single(_insertion.Requests).Text);
    }

    [Fact]
    public async Task PaletteChoice_WhenTheFieldDoesNotComeBack_InsertsNothing()
    {
        await StartAsync();
        _resolver.ActivateResult = false;

        _engine.InsertFromPalette("s-cc", FakeResolver.Notepad);
        await SettleAsync();

        Assert.Empty(_insertion.Requests);
        Assert.Contains(_sink.Events, e => e is TargetRejected { Reason: RejectionReason.WindowChanged });
    }

    [Fact]
    public async Task PaletteChoice_IntoAPasswordField_InsertsNothing()
    {
        await StartAsync();
        var password = FakeResolver.Notepad with { Control = FakeResolver.Notepad.Control with { IsPassword = true } };
        _resolver.Target = password;

        _engine.InsertFromPalette("s-cc", password);
        await SettleAsync();

        Assert.Empty(_insertion.Requests);
        Assert.Contains(_sink.Events, e => e is TargetRejected { Reason: RejectionReason.PolicyDenied });
    }

    [Fact]
    public async Task PaletteChoice_OfATemplate_OpensItsFields()
    {
        await StartAsync();

        _engine.InsertFromPalette("s-s1", FakeResolver.Notepad);
        await SettleAsync();

        Assert.Equal(["cliente"], _fields.Shown!.Select(f => f.Name));
    }

    [Fact]
    public async Task PaletteChoice_OfAnUnknownSnippet_DoesNothing()
    {
        await StartAsync();

        _engine.InsertFromPalette("gone", FakeResolver.Notepad);
        await SettleAsync();

        Assert.Equal(0, _resolver.Activations);
        Assert.Empty(_insertion.Requests);
    }
}
