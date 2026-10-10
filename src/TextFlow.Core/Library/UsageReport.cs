namespace TextFlow.Core.Library;

/// <summary>How often a snippet was used (D12). No content: a count and the last time.</summary>
public sealed record SnippetUsage(int Uses, DateTimeOffset LastUsedAt);

/// <param name="GroupName">Name of the group that holds the snippet; empty at the library root.</param>
public sealed record UsedSnippet(LibrarySnippet Snippet, string GroupName, int Uses, DateTimeOffset LastUsedAt);

/// <summary>
/// Reads use counts against the current library. Menus keep their own order on purpose: a choice typed from memory
/// ("lc" + "1") must not move because another entry got popular.
/// </summary>
public static class UsageReport
{
    /// <summary>Most used first; ties go to the most recent. Snippets no longer in the library are skipped.</summary>
    public static IReadOnlyList<UsedSnippet> Top(LibraryGroup root, IReadOnlyDictionary<string, SnippetUsage> usage, int count)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(usage);
        return Snippets(root)
            .Where(entry => usage.ContainsKey(entry.Snippet.Id))
            .Select(entry => (entry, use: usage[entry.Snippet.Id]))
            .OrderByDescending(x => x.use.Uses)
            .ThenByDescending(x => x.use.LastUsedAt)
            .Take(count)
            .Select(x => new UsedSnippet(x.entry.Snippet, x.entry.GroupName, x.use.Uses, x.use.LastUsedAt))
            .ToList();
    }

    /// <summary>Enabled, insertable snippets not used in the last <paramref name="days"/> days (or never).</summary>
    public static IReadOnlyList<LibrarySnippet> Unused(
        LibraryGroup root, IReadOnlyDictionary<string, SnippetUsage> usage, DateTimeOffset now, int days)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(usage);
        var since = now.AddDays(-days);
        return Snippets(root)
            .Select(entry => entry.Snippet)
            .Where(s => s.Enabled && !s.IsInfoOnly)
            .Where(s => !usage.TryGetValue(s.Id, out var use) || use.LastUsedAt < since)
            .ToList();
    }

    private static IEnumerable<(LibrarySnippet Snippet, string GroupName)> Snippets(LibraryGroup group, bool isRoot = true) =>
        group.Snippets.Select(s => (s, isRoot ? string.Empty : group.Name))
            .Concat(group.Groups.SelectMany(g => Snippets(g, isRoot: false)));
}
