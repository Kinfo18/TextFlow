using TextFlow.Core.Diagnostics;
using TextFlow.Core.Input;
using TextFlow.Core.Menus;

namespace TextFlow.Core.Tests.Engine;

/// <summary>
/// The group menu is drawn only after a short delay (issue: 79 % of menus were closed because the user kept typing
/// a word that starts with a group abbreviation). Menu mode starts at once, so a choice typed from memory still works.
/// </summary>
public sealed partial class ExpansionEngineTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    private async Task StartWithMenuDelayAsync()
    {
        _engine = CreateEngine(Delay);
        await StartAsync();
    }

    private async Task ElapseMenuDelayAsync()
    {
        _time.Advance(Delay);
        await SettleAsync();
    }

    [Fact]
    public void DefaultMenuDelay_Is250Ms()
    {
        Assert.Equal(Delay, Core.Engine.ExpansionEngineOptions.Default.MenuDelay);
    }

    [Fact]
    public async Task MenuTrigger_EntersMenuModeAtOnce_ButShowsTheMenuOnlyAfterTheDelay()
    {
        await StartWithMenuDelayAsync();

        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        Assert.True(_hook.MenuMode);
        Assert.Null(_menu.Shown);
        Assert.DoesNotContain(_sink.Events, e => e is MenuShown);

        await ElapseMenuDelayAsync();

        Assert.Equal("LC", _menu.Shown?.Trigger);
        Assert.Contains(_sink.Events, e => e is MenuShown { AnchoredToCaret: true });
    }

    [Fact]
    public async Task KeepTypingBeforeTheDelay_NeverShowsTheMenu_AndLeavesMenuMode()
    {
        await StartWithMenuDelayAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        await TypeAsync(new MenuInterrupted());
        await ElapseMenuDelayAsync();

        Assert.Null(_menu.Shown);
        Assert.False(_hook.MenuMode);
        Assert.Equal(0, _menu.Dismissals);
        Assert.DoesNotContain(_sink.Events, e => e is MenuShown or MenuClosed);
    }

    [Fact]
    public async Task ClickBeforeTheDelay_NeverShowsTheMenu()
    {
        await StartWithMenuDelayAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        await TypeAsync(new MenuInterrupted(10, 10));
        await ElapseMenuDelayAsync();

        Assert.Null(_menu.Shown);
        Assert.False(_hook.MenuMode);
    }

    [Fact]
    public async Task ChoiceTypedFromMemoryBeforeTheDelay_ShowsTheMenuAtOnce_AndForwardsTheKey()
    {
        await StartWithMenuDelayAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        await TypeAsync(new MenuKeyPressed(MenuInput.Number(1)));

        Assert.Equal("LC", _menu.Shown?.Trigger);
        Assert.Equal([MenuInput.Number(1)], _menu.Inputs);
    }

    [Fact]
    public async Task EscapeBeforeTheDelay_ClosesSilently()
    {
        await StartWithMenuDelayAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        await TypeAsync(new MenuKeyPressed(MenuInput.Escape));
        await ElapseMenuDelayAsync();

        Assert.Null(_menu.Shown);
        Assert.False(_hook.MenuMode);
        Assert.DoesNotContain(_sink.Events, e => e is MenuShown or MenuClosed);
    }

    [Fact]
    public async Task ForegroundChangeBeforeTheDelay_NeverShowsTheMenu()
    {
        await StartWithMenuDelayAsync();
        await TypeAsync(new TriggerTyped(Match("LC"), FakeResolver.Window));

        await TypeAsync(new ForegroundChanged(FakeResolver.Window + 1));
        await ElapseMenuDelayAsync();

        Assert.Null(_menu.Shown);
        Assert.False(_hook.MenuMode);
        Assert.DoesNotContain(_sink.Events, e => e is MenuClosed);
    }

    [Fact]
    public async Task LongerTriggerBeforeTheDelay_ExpandsIt_WithoutEverShowingTheMenu()
    {
        await StartWithMenuDelayAsync();
        await TypeAsync(new TriggerPending(Match("cp"), 7, FakeResolver.Window));

        await TypeAsync(new TriggerTyped(Match("cp1"), FakeResolver.Window));
        await ElapseMenuDelayAsync();

        Assert.Null(_menu.Shown);
        Assert.Contains(_sink.Events, e => e is ExpansionCompleted { FromMenu: false });
        Assert.DoesNotContain(_sink.Events, e => e is MenuShown or MenuClosed);
    }
}
