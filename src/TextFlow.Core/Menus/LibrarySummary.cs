using TextFlow.Core.Import;
using TextFlow.Core.Library;

namespace TextFlow.Core.Menus;

/// <summary>Counts only (safe to log and show): what an imported library contains.</summary>
/// <param name="Commands">All snippets, info notes included, as aText counts them.</param>
/// <param name="DirectTriggers">Typed abbreviations that expand directly (a snippet may have several).</param>
public sealed record LibrarySummary(int Menus, int Commands, int DirectTriggers, int Issues)
{
    public static LibrarySummary Of(ATextImport import, LibraryIndex index)
    {
        ArgumentNullException.ThrowIfNull(import);
        return Of(import.Root, index, import.Issues.Count);
    }

    /// <param name="issues">Warnings of the import that produced this library (0 when edited in TextFlow).</param>
    public static LibrarySummary Of(LibraryGroup root, LibraryIndex index, int issues)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(index);

        return new LibrarySummary(index.Menus.Count, CountSnippets(root), index.Triggers.Count - index.Menus.Count, issues);
    }

    private static int CountSnippets(LibraryGroup group) => group.Snippets.Count + group.Groups.Sum(CountSnippets);
}
