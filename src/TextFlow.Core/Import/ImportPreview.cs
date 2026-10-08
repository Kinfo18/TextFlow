using TextFlow.Core.Expansion;
using TextFlow.Core.Library;
using TextFlow.Core.Menus;

namespace TextFlow.Core.Import;

/// <summary>
/// What importing an aText backup would do (H2.3), shown before anything is written. Counts only: no snippet
/// content, abbreviations or names, so the report is safe to show anywhere.
/// </summary>
/// <param name="Groups">Groups in the backup, the root excluded.</param>
/// <param name="WaitingPrefixes">Abbreviations that wait 600 ms because a longer one starts the same way ("dir" → "dir1").</param>
/// <param name="ReplacesExisting">The current library has content that the import replaces.</param>
/// <param name="Added">Snippets (by id) in the backup but not in the current library.</param>
/// <param name="Removed">Snippets in the current library that the import drops.</param>
/// <param name="Changed">Snippets in both whose content, abbreviations, mode or enabled state differ.</param>
public sealed record ImportPreview(
    LibrarySummary Incoming,
    int Groups,
    int WaitingPrefixes,
    int RichTextAsPlain,
    int DuplicateAbbreviations,
    int OtherIssues,
    bool ReplacesExisting,
    int Added,
    int Removed,
    int Changed)
{
    public static ImportPreview Of(ATextImport import, LibraryGroup current)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(current);

        var index = LibraryIndex.Build(import.Root);
        var incoming = Snippets(import.Root).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var existing = Snippets(current).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var rich = import.Issues.Count(i => i.Code == ImportIssueCode.RichTextImportedAsPlain);
        var duplicates = import.Issues.Count(i => i.Code == ImportIssueCode.DuplicateAbbreviation);

        return new ImportPreview(
            LibrarySummary.Of(import, index),
            AllGroups(import.Root).Count() - 1,
            CountWaitingPrefixes(index.Triggers),
            rich,
            duplicates,
            import.Issues.Count - rich - duplicates,
            ReplacesExisting: existing.Count > 0 || current.Groups.Count > 0,
            Added: incoming.Keys.Count(id => !existing.ContainsKey(id)),
            Removed: existing.Keys.Count(id => !incoming.ContainsKey(id)),
            Changed: incoming.Count(pair => existing.TryGetValue(pair.Key, out var before) && Differs(before, pair.Value)));
    }

    private static int CountWaitingPrefixes(IReadOnlyList<TriggerDefinition> triggers) =>
        triggers.Count(shorter => shorter.Mode == TriggerMode.Immediate && triggers.Any(longer =>
            longer.Trigger.Length > shorter.Trigger.Length
            && longer.Trigger.StartsWith(
                shorter.Trigger,
                shorter.IgnoreCase || longer.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));

    private static bool Differs(LibrarySnippet before, LibrarySnippet after) =>
        before.Content != after.Content
        || before.Name != after.Name
        || before.IsRichText != after.IsRichText
        || before.Mode != after.Mode
        || before.Enabled != after.Enabled
        || before.SendEnter != after.SendEnter
        || !before.Abbreviations.SequenceEqual(after.Abbreviations, StringComparer.Ordinal);

    private static IEnumerable<LibraryGroup> AllGroups(LibraryGroup group) => group.Groups.SelectMany(AllGroups).Prepend(group);

    private static IEnumerable<LibrarySnippet> Snippets(LibraryGroup group) => AllGroups(group).SelectMany(g => g.Snippets);
}
