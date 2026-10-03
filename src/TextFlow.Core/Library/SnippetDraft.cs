using TextFlow.Core.Expansion;

namespace TextFlow.Core.Library;

public enum SnippetDraftError
{
    /// <summary>Without an abbreviation the snippet can only be found by name in a menu: it needs one of the two.</summary>
    NeedsAbbreviationOrName,

    /// <summary>Longer than the matcher buffer: it could never be typed.</summary>
    AbbreviationTooLong,

    /// <summary>An after-delimiter abbreviation with a space or punctuation could never be completed.</summary>
    DelimiterInAbbreviation,
}

/// <summary>
/// The snippet editor's form (H3.3) as plain values, validated in Core. Abbreviations are one per line so they may
/// contain spaces ("Foto valida"), as in aText.
/// </summary>
public sealed record SnippetDraft(
    string Id,
    string AbbreviationsText,
    string Name,
    string Content,
    bool IsRichText,
    SnippetMode Mode,
    bool Enabled)
{
    private static readonly int MaxAbbreviationLength = TriggerOptions.Default.MaxBufferLength - 1;

    public static SnippetDraft New() =>
        new($"tf-{Guid.NewGuid():N}", string.Empty, string.Empty, string.Empty, IsRichText: false, SnippetMode.Immediate, Enabled: true);

    public static SnippetDraft From(LibrarySnippet snippet)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        return new SnippetDraft(
            snippet.Id, string.Join('\n', snippet.Abbreviations), snippet.Name, snippet.Content, snippet.IsRichText, snippet.Mode, snippet.Enabled);
    }

    public IReadOnlyList<string> Abbreviations =>
        AbbreviationsText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim('\r', ' ', '\t'))
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <returns>The snippet to save, or null with the reasons it cannot be saved.</returns>
    public (LibrarySnippet? Snippet, IReadOnlyList<SnippetDraftError> Errors) ToSnippet()
    {
        var abbreviations = Abbreviations;
        var name = !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : abbreviations.Count > 0 ? abbreviations[0] : string.Empty;
        var errors = new List<SnippetDraftError>();

        if (name.Length == 0)
        {
            errors.Add(SnippetDraftError.NeedsAbbreviationOrName);
        }

        if (abbreviations.Any(a => a.Length > MaxAbbreviationLength))
        {
            errors.Add(SnippetDraftError.AbbreviationTooLong);
        }

        if (Mode == SnippetMode.AfterDelimiter && abbreviations.Any(a => a.Any(c => char.IsWhiteSpace(c) || TriggerOptions.Default.Delimiters.Contains(c))))
        {
            errors.Add(SnippetDraftError.DelimiterInAbbreviation);
        }

        return errors.Count > 0
            ? (null, errors)
            : (new LibrarySnippet(Id, name, Content, IsRichText, abbreviations, Mode, Enabled), errors);
    }
}
