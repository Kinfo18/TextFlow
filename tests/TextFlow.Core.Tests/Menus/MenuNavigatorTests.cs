using TextFlow.Core.Menus;

namespace TextFlow.Core.Tests.Menus;

public class MenuNavigatorTests
{
    private static readonly MenuSnippetEntry First = new("Primero", "uno");
    private static readonly MenuSnippetEntry Note = new("Nota informativa", string.Empty);
    private static readonly MenuSnippetEntry Second = new("Segundo", "dos");
    private static readonly MenuSnippetEntry Deep = new("Profundo", "tres");
    private static readonly MenuGroupEntry Sub = new("Subgrupo", [Deep]);

    private static readonly GroupMenu Menu = new("m", "LC", IgnoreCase: true, [Sub, First, Note, Second]);

    private static MenuStep Press(MenuState state, params MenuInput[] inputs)
    {
        var step = new MenuStep(state);
        foreach (var input in inputs)
        {
            step = MenuNavigator.Apply(step.State, input);
        }

        return step;
    }

    [Fact]
    public void Open_SelectsFirstSelectableEntry()
    {
        var state = MenuNavigator.Open(Menu);

        Assert.Equal(0, state.Current.Selected);
        Assert.Equal("LC", state.Current.Title);
    }

    [Fact]
    public void Open_SkipsLeadingInfoEntries()
    {
        var state = MenuNavigator.Open(Menu with { Entries = [Note, First] });

        Assert.Equal(1, state.Current.Selected);
    }

    [Fact]
    public void Down_SkipsInfoEntries_AndWraps()
    {
        var state = MenuNavigator.Open(Menu);

        Assert.Equal(1, Press(state, MenuInput.Down).State.Current.Selected);
        Assert.Equal(3, Press(state, MenuInput.Down, MenuInput.Down).State.Current.Selected);
        Assert.Equal(0, Press(state, MenuInput.Down, MenuInput.Down, MenuInput.Down).State.Current.Selected);
    }

    [Fact]
    public void Up_FromFirst_WrapsToLastSelectable()
    {
        Assert.Equal(3, Press(MenuNavigator.Open(Menu), MenuInput.Up).State.Current.Selected);
    }

    [Fact]
    public void Enter_OnSnippet_ChoosesIt()
    {
        var step = Press(MenuNavigator.Open(Menu), MenuInput.Down, MenuInput.Enter);

        Assert.Same(First, step.Chosen);
        Assert.True(step.IsFinished);
    }

    [Theory]
    [InlineData(MenuInputKind.Enter)]
    [InlineData(MenuInputKind.Right)]
    public void EnterOrRight_OnGroup_OpensSubmenu(MenuInputKind kind)
    {
        var step = Press(MenuNavigator.Open(Menu), new MenuInput(kind));

        Assert.Null(step.Chosen);
        Assert.Equal(2, step.State.Path.Count);
        Assert.Equal("Subgrupo", step.State.Current.Title);
        Assert.Same(Deep, step.State.Current.Entries[0]);
    }

    [Fact]
    public void Left_InSubmenu_GoesBack_KeepingParentSelection()
    {
        var step = Press(MenuNavigator.Open(Menu), MenuInput.Enter, MenuInput.Left);

        Assert.Single(step.State.Path);
        Assert.Equal(0, step.State.Current.Selected);
    }

    [Fact]
    public void Left_AtRoot_DoesNothing()
    {
        var step = Press(MenuNavigator.Open(Menu), MenuInput.Left);

        Assert.False(step.IsFinished);
        Assert.Single(step.State.Path);
    }

    [Fact]
    public void Escape_Closes_WithoutChoice()
    {
        var step = Press(MenuNavigator.Open(Menu), MenuInput.Enter, MenuInput.Escape);

        Assert.True(step.IsFinished);
        Assert.Null(step.Chosen);
    }

    [Fact]
    public void Number_ActivatesNthSelectableEntry_IgnoringInfoEntries()
    {
        var state = MenuNavigator.Open(Menu);

        Assert.Same(First, Press(state, MenuInput.Number(2)).Chosen);
        Assert.Same(Second, Press(state, MenuInput.Number(3)).Chosen);
    }

    [Fact]
    public void Number_OnGroup_OpensIt()
    {
        Assert.Equal("Subgrupo", Press(MenuNavigator.Open(Menu), MenuInput.Number(1)).State.Current.Title);
    }

    [Fact]
    public void Number_OutOfRange_DoesNothing()
    {
        var step = Press(MenuNavigator.Open(Menu), MenuInput.Number(9));

        Assert.False(step.IsFinished);
        Assert.Equal(0, step.State.Current.Selected);
    }

    [Fact]
    public void NumberOf_ReturnsShortcutForSelectableEntries_AndNullForInfo()
    {
        var level = MenuNavigator.Open(Menu).Current;

        Assert.Equal([1, 2, null, 3], level.Entries.Select(level.NumberOf));
    }

    [Fact]
    public void Activate_ByIndex_SupportsMouseClicks()
    {
        var state = MenuNavigator.Open(Menu);

        Assert.Same(Second, MenuNavigator.Activate(state, 3).Chosen);
        Assert.False(MenuNavigator.Activate(state, 2).IsFinished); // info entry: no-op
    }

    [Fact]
    public void MenuWithOnlyInfoEntries_HasNoSelection_AndEnterDoesNothing()
    {
        var state = MenuNavigator.Open(Menu with { Entries = [Note] });

        Assert.Equal(-1, state.Current.Selected);
        Assert.False(Press(state, MenuInput.Enter).IsFinished);
    }

    [Fact]
    public void Apply_DoesNotMutatePreviousState()
    {
        var state = MenuNavigator.Open(Menu);

        Press(state, MenuInput.Down, MenuInput.Enter);

        Assert.Equal(0, state.Current.Selected);
    }
}
