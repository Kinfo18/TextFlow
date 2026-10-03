using TextFlow.Core.Import;

namespace TextFlow.Core.Menus;

/// <summary>Counts only (safe to log and show): what an imported library contains.</summary>
/// <param name="Commands">All snippets, info notes included, as aText counts them.</param>
/// <param name="DirectTriggers">Typed abbreviations that expand directly (a snippet may have several).</param>
public sealed record LibrarySummary(int Menus, int Commands, int DirectTriggers, int Issues)
{
    public static LibrarySummary Of(ATextImport import, LibraryIndex index)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(index);

        return new LibrarySummary(
            index.Menus.Count,
            CountSnippets(import.Root),
            index.Triggers.Count - index.Menus.Count,
            import.Issues.Count);
    }

    private static int CountSnippets(ImportedGroup group) => group.Snippets.Count + group.Groups.Sum(CountSnippets);
}
