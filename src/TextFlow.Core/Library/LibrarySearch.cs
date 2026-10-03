using System.Globalization;
using System.Text;

namespace TextFlow.Core.Library;

/// <param name="GroupPath">Names from the top-level group down to the snippet's group (root excluded).</param>
public sealed record SearchHit(LibrarySnippet Snippet, string GroupId, IReadOnlyList<string> GroupPath);

/// <summary>
/// Library search for the editor (H3.2): by abbreviation, name or content, ignoring case and accents. Ranked so the
/// snippet you are typing the abbreviation of comes first ("orca3" finds orca3 before anything that mentions it).
/// In memory only: queries and results are never logged.
/// </summary>
public static class LibrarySearch
{
    private const int MaxResults = 200;

    private enum Rank
    {
        ExactAbbreviation,
        AbbreviationPrefix,
        Abbreviation,
        Name,
        Content,
    }

    public static IReadOnlyList<SearchHit> Find(LibraryGroup root, string query)
    {
        ArgumentNullException.ThrowIfNull(root);
        var needle = Fold(query ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            return [];
        }

        var hits = new List<(Rank Rank, int Order, SearchHit Hit)>();
        Visit(root, [], needle, hits);
        return hits
            .OrderBy(h => h.Rank)
            .ThenBy(h => h.Order)
            .Take(MaxResults)
            .Select(h => h.Hit)
            .ToArray();
    }

    private static void Visit(LibraryGroup group, IReadOnlyList<string> path, string needle, List<(Rank, int, SearchHit)> hits)
    {
        foreach (var snippet in group.Snippets)
        {
            if (RankOf(snippet, needle) is { } rank)
            {
                hits.Add((rank, hits.Count, new SearchHit(snippet, group.Id, path)));
            }
        }

        foreach (var child in group.Groups)
        {
            Visit(child, [.. path, child.Name], needle, hits);
        }
    }

    private static Rank? RankOf(LibrarySnippet snippet, string needle)
    {
        var abbreviations = snippet.Abbreviations.Select(Fold).ToArray();
        if (abbreviations.Any(a => a == needle))
        {
            return Rank.ExactAbbreviation;
        }

        if (abbreviations.Any(a => a.StartsWith(needle, StringComparison.Ordinal)))
        {
            return Rank.AbbreviationPrefix;
        }

        if (abbreviations.Any(a => a.Contains(needle, StringComparison.Ordinal)))
        {
            return Rank.Abbreviation;
        }

        if (Fold(snippet.Name).Contains(needle, StringComparison.Ordinal))
        {
            return Rank.Name;
        }

        return Fold(snippet.Content).Contains(needle, StringComparison.Ordinal) ? Rank.Content : null;
    }

    /// <summary>Lower case without diacritics: "Canción" and "CANCION" compare equal.</summary>
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
