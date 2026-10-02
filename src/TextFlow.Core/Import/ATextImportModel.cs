namespace TextFlow.Core.Import;

/// <param name="IsRichText">aText stored it as rich text ("h"); imported as plain text for now.</param>
public sealed record ImportedSnippet(string Id, string Name, string Content, bool IsRichText)
{
    /// <summary>Empty snippets are notes the user reads in the group menu; selecting one inserts nothing.</summary>
    public bool IsInfoOnly => Content.Length == 0;
}

/// <param name="Abbreviation">
/// Typing it opens a menu with the group's snippets and subgroups (aText group abbreviation).
/// Groups sharing an abbreviation are listed together in one menu.
/// </param>
public sealed record ImportedGroup(
    string Id,
    string Name,
    string? Abbreviation,
    bool IgnoreCase,
    IReadOnlyList<ImportedGroup> Groups,
    IReadOnlyList<ImportedSnippet> Snippets);

public enum ImportIssueCode
{
    MissingName,
    RichTextImportedAsPlain,
    DuplicateAbbreviation,
    UnknownField,
}

/// <param name="Detail">Names and field keys only; never snippet content.</param>
public sealed record ImportIssue(ImportIssueCode Code, string ItemId, string Detail);

public sealed record ATextImport(ImportedGroup Root, IReadOnlyList<ImportIssue> Issues);
