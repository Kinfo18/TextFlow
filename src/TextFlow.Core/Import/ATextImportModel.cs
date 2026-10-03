using TextFlow.Core.Library;

namespace TextFlow.Core.Import;

public enum ImportIssueCode
{
    MissingName,
    RichTextImportedAsPlain,
    DuplicateAbbreviation,
    UnknownField,
}

/// <param name="Detail">Names and field keys only; never snippet content.</param>
public sealed record ImportIssue(ImportIssueCode Code, string ItemId, string Detail);

public sealed record ATextImport(LibraryGroup Root, IReadOnlyList<ImportIssue> Issues);
