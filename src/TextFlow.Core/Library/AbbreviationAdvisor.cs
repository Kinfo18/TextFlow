namespace TextFlow.Core.Library;

public enum AbbreviationWarningKind
{
    /// <summary>Another command already uses it: only one of them can expand.</summary>
    UsedByCommand,

    /// <summary>A group menu uses it: the menu wins and the command will not expand.</summary>
    UsedByMenu,

    /// <summary>A longer abbreviation starts the same way, so this one waits 0.6 s before expanding.</summary>
    WaitsForLonger,

    /// <summary>It starts with an existing shorter abbreviation, which will now wait 0.6 s.</summary>
    MakesShorterWait,

    /// <summary>A common word: an immediate abbreviation would fire while typing ordinary text.</summary>
    CommonWord,
}

/// <param name="Other">Name of the command or group it clashes with.</param>
/// <param name="OtherAbbreviation">Their abbreviation.</param>
public sealed record AbbreviationWarning(AbbreviationWarningKind Kind, string Abbreviation, string? Other = null, string? OtherAbbreviation = null);

/// <summary>
/// Live advice while typing an abbreviation in the editor (H3.5). Warnings never block saving: the user may want a
/// clash on purpose. Follows the matcher's rules: case is ignored when either side's group ignores it, menus win
/// over commands, and only immediate triggers wait for longer ones or fire inside words.
/// </summary>
public static class AbbreviationAdvisor
{
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Spanish
        "a", "al", "algo", "ante", "así", "como", "con", "cual", "de", "del", "desde", "donde", "el", "ella", "en", "entre", "era",
        "es", "esa", "ese", "eso", "esta", "este", "esto", "está", "fue", "ha", "hay", "la", "las", "le", "les", "lo", "los", "me",
        "mi", "muy", "más", "nada", "ni", "no", "nos", "o", "para", "pero", "por", "que", "qué", "se", "ser", "si", "sin", "sobre",
        "son", "su", "sus", "sí", "también", "te", "todo", "tu", "tú", "un", "una", "uno", "y", "ya", "yo",
        // English
        "an", "and", "are", "as", "at", "be", "by", "for", "from", "have", "he", "i", "in", "is", "it", "of", "on", "or", "the",
        "this", "to", "was", "we", "with", "you",
    };

    public static IReadOnlyList<AbbreviationWarning> ForSnippet(
        LibraryGroup root, string? snippetId, string groupId, IReadOnlyList<string> abbreviations, SnippetMode mode)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(abbreviations);
        var ignoreCase = FindGroup(root, groupId)?.IgnoreCase ?? true;
        var others = Triggers(root).Where(t => t.SnippetId is null || t.SnippetId != snippetId).ToArray();
        return abbreviations.SelectMany(a => Check(a, ignoreCase, mode == SnippetMode.Immediate, others)).ToArray();
    }

    public static IReadOnlyList<AbbreviationWarning> ForGroup(LibraryGroup root, string groupId, string abbreviation, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(abbreviation))
        {
            return [];
        }

        var others = Triggers(root).Where(t => t.GroupId != groupId).ToArray();
        return Check(abbreviation.Trim(), ignoreCase, immediate: true, others).ToArray();
    }

    private static IEnumerable<AbbreviationWarning> Check(string abbreviation, bool ignoreCase, bool immediate, Trigger[] others)
    {
        if (immediate && CommonWords.Contains(abbreviation))
        {
            yield return new AbbreviationWarning(AbbreviationWarningKind.CommonWord, abbreviation);
        }

        foreach (var other in others)
        {
            var comparison = ignoreCase || other.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(abbreviation, other.Abbreviation, comparison))
            {
                yield return new AbbreviationWarning(
                    other.IsMenu ? AbbreviationWarningKind.UsedByMenu : AbbreviationWarningKind.UsedByCommand, abbreviation, other.Name, other.Abbreviation);
            }
            else if (immediate && other.Abbreviation.Length > abbreviation.Length && other.Abbreviation.StartsWith(abbreviation, comparison))
            {
                yield return new AbbreviationWarning(AbbreviationWarningKind.WaitsForLonger, abbreviation, other.Name, other.Abbreviation);
            }
            else if (other.Immediate && abbreviation.Length > other.Abbreviation.Length && abbreviation.StartsWith(other.Abbreviation, comparison))
            {
                yield return new AbbreviationWarning(AbbreviationWarningKind.MakesShorterWait, abbreviation, other.Name, other.Abbreviation);
            }
        }
    }

    private sealed record Trigger(string Abbreviation, bool IgnoreCase, bool Immediate, bool IsMenu, string Name, string? SnippetId, string? GroupId);

    /// <summary>Every abbreviation that can fire: group menus plus enabled, non-note commands.</summary>
    private static IEnumerable<Trigger> Triggers(LibraryGroup group)
    {
        if (group.Abbreviation is { Length: > 0 } menu)
        {
            yield return new Trigger(menu, group.IgnoreCase, Immediate: true, IsMenu: true, group.Name, null, group.Id);
        }

        foreach (var snippet in group.Snippets.Where(s => s.Enabled && !s.IsInfoOnly))
        {
            foreach (var abbreviation in snippet.Abbreviations)
            {
                yield return new Trigger(
                    abbreviation, group.IgnoreCase, snippet.Mode == SnippetMode.Immediate, IsMenu: false, snippet.Name, snippet.Id, null);
            }
        }

        foreach (var trigger in group.Groups.SelectMany(Triggers))
        {
            yield return trigger;
        }
    }

    private static LibraryGroup? FindGroup(LibraryGroup group, string id) =>
        group.Id == id ? group : group.Groups.Select(child => FindGroup(child, id)).FirstOrDefault(found => found is not null);
}
