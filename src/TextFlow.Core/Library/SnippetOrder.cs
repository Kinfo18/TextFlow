namespace TextFlow.Core.Library;

/// <summary>Where a command goes when the user moves it inside its group without dragging (menu or Alt+arrows).</summary>
public enum OrderMove
{
    Up,
    Down,
    First,
    Last,
}

/// <summary>New snippet orders for a group; the input list is never changed.</summary>
public static class SnippetOrder
{
    /// <returns>The reordered ids; null when the id is unknown or already at that end (nothing to store).</returns>
    public static IReadOnlyList<string>? Move(IReadOnlyList<string> ids, string id, OrderMove move)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var from = ids.ToList().IndexOf(id);
        if (from < 0)
        {
            return null;
        }

        var to = move switch
        {
            OrderMove.Up => from - 1,
            OrderMove.Down => from + 1,
            OrderMove.First => 0,
            _ => ids.Count - 1,
        };
        if (to < 0 || to >= ids.Count || to == from)
        {
            return null;
        }

        var result = ids.Where((_, i) => i != from).ToList();
        result.Insert(to, id);
        return result;
    }
}
