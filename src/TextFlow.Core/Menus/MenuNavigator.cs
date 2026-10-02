namespace TextFlow.Core.Menus;

/// <summary>
/// Keyboard/mouse navigation of a <see cref="GroupMenu"/> as pure functions over <see cref="MenuState"/>:
/// arrows move (skipping info entries, wrapping), Enter/Right open a group or choose a snippet,
/// Left goes back, Escape closes, 1-9 activate the nth selectable entry.
/// </summary>
public static class MenuNavigator
{
    public const int MaxQuickPick = 9;

    public static MenuState Open(GroupMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        return new MenuState([Level(menu.Trigger, menu.Entries)]);
    }

    public static MenuStep Apply(MenuState state, MenuInput input)
    {
        ArgumentNullException.ThrowIfNull(state);
        var level = state.Current;

        return input.Kind switch
        {
            MenuInputKind.Up => new MenuStep(Replace(state, level with { Selected = Move(level, -1) })),
            MenuInputKind.Down => new MenuStep(Replace(state, level with { Selected = Move(level, +1) })),
            MenuInputKind.Left => new MenuStep(state.Path.Count > 1 ? new MenuState(state.Path.Take(state.Path.Count - 1).ToArray()) : state),
            MenuInputKind.Right => level.Selected >= 0 && level.Entries[level.Selected] is MenuGroupEntry
                ? Activate(state, level.Selected)
                : new MenuStep(state),
            MenuInputKind.Enter => level.Selected >= 0 ? Activate(state, level.Selected) : new MenuStep(state),
            MenuInputKind.Escape => new MenuStep(state, Closed: true),
            MenuInputKind.Number => ActivateNumber(state, input.Value),
            _ => new MenuStep(state),
        };
    }

    /// <summary>Activates the entry at <paramref name="index"/> of the visible level (mouse click).</summary>
    public static MenuStep Activate(MenuState state, int index)
    {
        ArgumentNullException.ThrowIfNull(state);
        var level = state.Current;
        if (index < 0 || index >= level.Entries.Count || !level.Entries[index].IsSelectable)
        {
            return new MenuStep(state);
        }

        var entered = Replace(state, level with { Selected = index });
        return level.Entries[index] switch
        {
            MenuGroupEntry group => new MenuStep(new MenuState([.. entered.Path, Level(group.Label, group.Children)])),
            MenuSnippetEntry snippet => new MenuStep(entered, Chosen: snippet),
            _ => new MenuStep(state),
        };
    }

    private static MenuStep ActivateNumber(MenuState state, int number)
    {
        if (number is < 1 or > MaxQuickPick)
        {
            return new MenuStep(state);
        }

        var entries = state.Current.Entries;
        var seen = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsSelectable && ++seen == number)
            {
                return Activate(state, i);
            }
        }

        return new MenuStep(state);
    }

    private static MenuLevel Level(string title, IReadOnlyList<MenuEntry> entries)
    {
        var first = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].IsSelectable)
            {
                first = i;
                break;
            }
        }

        return new MenuLevel(title, entries, first);
    }

    private static int Move(MenuLevel level, int direction)
    {
        if (level.Selected < 0)
        {
            return -1;
        }

        var count = level.Entries.Count;
        var index = level.Selected;
        for (var step = 0; step < count; step++)
        {
            index = (index + direction + count) % count;
            if (level.Entries[index].IsSelectable)
            {
                return index;
            }
        }

        return level.Selected;
    }

    private static MenuState Replace(MenuState state, MenuLevel current) =>
        new([.. state.Path.Take(state.Path.Count - 1), current]);
}
