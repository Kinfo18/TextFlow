namespace TextFlow.Core.Menus;

public abstract record MenuEntry(string Label)
{
    /// <summary>False for info-only entries: they are shown but cannot be selected or numbered.</summary>
    public virtual bool IsSelectable => true;
}

public sealed record MenuGroupEntry(string Label, IReadOnlyList<MenuEntry> Children) : MenuEntry(Label);

/// <remarks><see cref="Content"/> is user content: never log it.</remarks>
/// <param name="SendEnter">Enter follows the text (<see cref="Library.LibrarySnippet.SendEnter"/>).</param>
public sealed record MenuSnippetEntry(string Label, string Content, bool SendEnter = false) : MenuEntry(Label)
{
    /// <summary>Empty snippets are notes the user reads in the menu (aText usage); they insert nothing.</summary>
    public bool IsInfoOnly => Content.Length == 0;

    public override bool IsSelectable => !IsInfoOnly;
}

/// <summary>Menu opened by typing <see cref="Trigger"/> (an aText group abbreviation).</summary>
public sealed record GroupMenu(string Id, string Trigger, bool IgnoreCase, IReadOnlyList<MenuEntry> Entries);

public enum MenuInputKind
{
    Up,
    Down,
    Left,
    Right,
    Enter,
    Escape,
    Number,
}

/// <param name="Value">1-9 for <see cref="MenuInputKind.Number"/>.</param>
public readonly record struct MenuInput(MenuInputKind Kind, int Value = 0)
{
    public static MenuInput Up => new(MenuInputKind.Up);

    public static MenuInput Down => new(MenuInputKind.Down);

    public static MenuInput Left => new(MenuInputKind.Left);

    public static MenuInput Right => new(MenuInputKind.Right);

    public static MenuInput Enter => new(MenuInputKind.Enter);

    public static MenuInput Escape => new(MenuInputKind.Escape);

    public static MenuInput Number(int value) => new(MenuInputKind.Number, value);
}

/// <param name="Selected">Index into <see cref="Entries"/>; -1 when no entry is selectable.</param>
public sealed record MenuLevel(string Title, IReadOnlyList<MenuEntry> Entries, int Selected)
{
    /// <summary>1-based quick-pick number of a selectable entry (1-9), null for info entries or beyond 9.</summary>
    public int? NumberOf(MenuEntry entry)
    {
        var number = 0;
        foreach (var candidate in Entries)
        {
            if (candidate.IsSelectable)
            {
                number++;
            }

            if (ReferenceEquals(candidate, entry))
            {
                return candidate.IsSelectable && number <= MenuNavigator.MaxQuickPick ? number : null;
            }
        }

        return null;
    }
}

/// <summary>Immutable navigation state: the root level first, the visible level last.</summary>
public sealed record MenuState(IReadOnlyList<MenuLevel> Path)
{
    public MenuLevel Current => Path[^1];
}

/// <param name="Chosen">Set when the user picked a snippet; the menu is finished.</param>
/// <param name="Closed">Set when the user dismissed the menu.</param>
public sealed record MenuStep(MenuState State, MenuSnippetEntry? Chosen = null, bool Closed = false)
{
    public bool IsFinished => Chosen is not null || Closed;
}
