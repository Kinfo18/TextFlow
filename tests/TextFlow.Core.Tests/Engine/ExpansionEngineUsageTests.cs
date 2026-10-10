using TextFlow.Contracts.Insertion;
using TextFlow.Core.Input;
using TextFlow.Core.Menus;

namespace TextFlow.Core.Tests.Engine;

/// <summary>Per-snippet use counts (D12): only the snippet id is recorded, never what it inserted.</summary>
public sealed partial class ExpansionEngineTests
{
    [Fact]
    public async Task DirectExpansion_RecordsTheSnippetId()
    {
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("cc"), FakeResolver.Window));

        Assert.Equal(["s-cc"], _usage.Recorded);
    }

    [Fact]
    public async Task FailedExpansion_IsNotCounted()
    {
        await StartAsync();
        _insertion.NextStatus = InsertionStatus.Failed;

        await TypeAsync(new TriggerTyped(Match("cc"), FakeResolver.Window));

        Assert.Empty(_usage.Recorded);
    }

    [Fact]
    public async Task ChoosingFromAMenu_RecordsTheChosenSnippet()
    {
        await StartAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        _menu.Finish(new MenuStep(MenuNavigator.Open(_menu.Shown!), Chosen: (MenuSnippetEntry)_menu.Shown!.Entries[0]));
        await SettleAsync();

        Assert.Equal(["s-nc"], _usage.Recorded);
    }

    [Fact]
    public async Task TemplateWithFields_CountsWhenItsFormOpens()
    {
        await StartAsync();

        await TypeAsync(new TriggerTyped(Match("s1"), FakeResolver.Window));

        Assert.NotNull(_fields.Shown);
        Assert.Equal(["s-s1"], _usage.Recorded);
    }
}
