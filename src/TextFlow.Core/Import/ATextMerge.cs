using TextFlow.Core.Library;

namespace TextFlow.Core.Import;

/// <param name="Root">The library with the backup added; written with one ReplaceAll, like an import.</param>
/// <param name="Added">Commands that were not in the library.</param>
/// <param name="Updated">Commands already in the library (same aText id) that the backup changes.</param>
/// <param name="AbbreviationClashes">New commands with an abbreviation the library already uses: the existing one keeps it.</param>
public sealed record MergedLibrary(LibraryGroup Root, int Added, int Updated, int AbbreviationClashes);

/// <summary>
/// Adds an aText backup to the library instead of replacing it (several aText libraries in one TextFlow).
/// Matching is by aText id: a command or group already in the library is updated where it is now (the user may have
/// moved it) and never duplicated; new commands of an existing group go last in it; everything else from the backup
/// goes last under the root, inside one group (the backup's own group, or one named after the file). The library root
/// keeps its name. Nothing is removed. Inputs are never changed.
/// </summary>
public static class ATextMerge
{
    public static MergedLibrary Append(LibraryGroup current, LibraryGroup incoming, string containerName, Func<string> newId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(newId);

        var context = new Context(current, incoming);
        var root = context.Update(current);
        if (!context.Matched.ContainsKey(incoming.Id))
        {
            var container = context.Prune(incoming);
            if (incoming.Id == ATextBackupReader.SyntheticRootId)
            {
                container = container with { Id = newId(), Name = containerName };
            }

            if (container.Groups.Count > 0 || container.Snippets.Count > 0)
            {
                root = root with { Groups = [.. root.Groups, container] };
            }
        }

        var before = AllSnippets(current).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var added = AllSnippets(root).Where(s => !before.ContainsKey(s.Id)).ToList();
        return new MergedLibrary(
            root,
            added.Count,
            AllSnippets(root).Count(s => before.TryGetValue(s.Id, out var old) && ImportPreview.Differs(old, s)),
            CountClashes(current, root, added));
    }

    private sealed class Context
    {
        private readonly LibraryGroup _root;
        private readonly HashSet<string> _currentSnippets;
        private readonly HashSet<string> _currentGroups;
        private readonly Dictionary<string, LibrarySnippet> _incomingSnippets;

        // Ids already placed in the result: a malformed backup that repeats an id must not insert it twice.
        private readonly HashSet<string> _placedSnippets;
        private readonly HashSet<string> _placedGroups;

        public Context(LibraryGroup current, LibraryGroup incoming)
        {
            _root = current;
            _currentSnippets = AllSnippets(current).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            _currentGroups = AllGroups(current).Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
            _placedSnippets = new HashSet<string>(_currentSnippets, StringComparer.Ordinal);
            _placedGroups = new HashSet<string>(_currentGroups, StringComparer.Ordinal);
            _incomingSnippets = AllSnippets(incoming).GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // The reader's made-up root is never a real group, and only the backup's own root may stand for the library root.
            Matched = AllGroups(incoming)
                .Where(g => _currentGroups.Contains(g.Id))
                .Where(g => ReferenceEquals(g, incoming) ? g.Id != ATextBackupReader.SyntheticRootId : g.Id != current.Id)
                .GroupBy(g => g.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }

        /// <summary>Backup groups that are already in the library, by id.</summary>
        public Dictionary<string, LibraryGroup> Matched { get; }

        /// <summary>An existing group with its commands updated and the backup's new children appended.</summary>
        public LibraryGroup Update(LibraryGroup group)
        {
            var source = Matched.GetValueOrDefault(group.Id);
            var updated = source is null || group.Id == _root.Id
                ? group
                : group with { Name = source.Name, Abbreviation = source.Abbreviation, IgnoreCase = source.IgnoreCase };
            return updated with
            {
                Snippets =
                [
                    .. group.Snippets.Select(s => _incomingSnippets.GetValueOrDefault(s.Id) ?? s),
                    .. source?.Snippets.Where(IsNew) ?? [],
                ],
                Groups =
                [
                    .. group.Groups.Select(Update),
                    .. source?.Groups.Where(IsNew).Select(Prune) ?? [],
                ],
            };
        }

        /// <summary>A new group with only what is new inside it: existing commands and groups stay where they are.</summary>
        public LibraryGroup Prune(LibraryGroup group) => group with
        {
            Snippets = group.Snippets.Where(IsNew).ToArray(),
            Groups = group.Groups.Where(IsNew).Select(Prune).ToArray(),
        };

        private bool IsNew(LibrarySnippet snippet) => _placedSnippets.Add(snippet.Id);

        private bool IsNew(LibraryGroup group) => _placedGroups.Add(group.Id);
    }

    /// <summary>A new command loses an abbreviation that an existing menu or command already answers to.</summary>
    private static int CountClashes(LibraryGroup current, LibraryGroup merged, IReadOnlyList<LibrarySnippet> added)
    {
        var addedIds = added.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var existing = Abbreviations(current).ToList();
        return Abbreviations(merged)
            .Where(entry => addedIds.Contains(entry.SnippetId))
            .GroupBy(entry => entry.SnippetId, StringComparer.Ordinal)
            .Count(snippet => snippet.Any(entry => existing.Any(old => string.Equals(
                old.Abbreviation,
                entry.Abbreviation,
                old.IgnoreCase || entry.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))));
    }

    /// <summary>Typeable abbreviations with the case rule of their group; group abbreviations share menus, so they are left out.</summary>
    private static IEnumerable<(string SnippetId, string Abbreviation, bool IgnoreCase)> Abbreviations(LibraryGroup root) =>
        AllGroups(root).SelectMany(g => g.Snippets.SelectMany(s => s.TypeableAbbreviations.Select(a => (s.Id, a, g.IgnoreCase))));

    private static IEnumerable<LibraryGroup> AllGroups(LibraryGroup group) => group.Groups.SelectMany(AllGroups).Prepend(group);

    private static IEnumerable<LibrarySnippet> AllSnippets(LibraryGroup group) => AllGroups(group).SelectMany(g => g.Snippets);
}
