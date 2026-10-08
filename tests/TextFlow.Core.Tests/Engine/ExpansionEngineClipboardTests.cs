using TextFlow.Core.Input;

namespace TextFlow.Core.Tests.Engine;

/// <summary><c>{{clipboard}}</c> (H5.3): what was copied when the trigger fired, never what the fields prompt copied later.</summary>
public sealed partial class ExpansionEngineTests
{
    [Fact]
    public async Task ClipboardVariable_InsertsTheCopiedText()
    {
        _variables.Clipboard = "pedido 4411";
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("clip1"), FakeResolver.Window));

        Assert.Equal("Copiado: pedido 4411", _insertion.Requests[^1].Text);
    }

    [Fact]
    public async Task ClipboardVariable_WithAnEmptyClipboard_InsertsNothingInItsPlace()
    {
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("clip1"), FakeResolver.Window));

        Assert.Equal("Copiado: ", _insertion.Requests[^1].Text);
    }

    [Fact]
    public async Task ClipboardVariable_WithFields_KeepsWhatWasCopiedBeforeTheTrigger()
    {
        _variables.Clipboard = "pedido 4411";
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("clipf"), FakeResolver.Window));

        _variables.Clipboard = "Ana"; // the user copies the field value
        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();

        Assert.Equal("Ana envió pedido 4411", _insertion.Requests[^1].Text);
    }

    [Fact]
    public async Task SnippetWithoutClipboardVariable_NeverReadsTheClipboard()
    {
        _variables.Clipboard = "secreto";
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));
        _fields.Finish(Values(("cliente", "Ana")));
        await SettleAsync();
        await TypeAsync(new TriggerTyped(Match("fecha"), FakeResolver.Window));

        Assert.Equal(0, _variables.ClipboardReads);
    }
}
