using TextFlow.Core.Import;
using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Import;

public sealed class ImportPreviewTests
{
    private static LibrarySnippet Snippet(string id, string abbreviation, string content = "x") => new(id, abbreviation, content, false, [abbreviation]);

    private static LibraryGroup Root(params LibraryGroup[] groups) => new("root", "root", null, true, groups, []);

    private static LibraryGroup Group(string id, string? abbreviation, params LibrarySnippet[] snippets) =>
        new(id, id, abbreviation, true, [], snippets);

    private static readonly LibraryGroup Empty = Root();

    [Fact]
    public void Of_CountsWhatTheBackupContains()
    {
        var incoming = Root(
            Group("g-t", "T1", Snippet("s-dir", "dir"), Snippet("s-dir1", "dir1"), Snippet("s-cc", "cc")),
            Group("g-x", null, Snippet("s-note", "Nota", content: "")));
        var import = new ATextImport(incoming,
        [
            new ImportIssue(ImportIssueCode.RichTextImportedAsPlain, "s-cc", "cc"),
            new ImportIssue(ImportIssueCode.RichTextImportedAsPlain, "s-dir", "dir"),
            new ImportIssue(ImportIssueCode.DuplicateAbbreviation, "s-dir1", "dir1"),
        ]);

        var preview = ImportPreview.Of(import, Empty);

        Assert.Equal(1, preview.Incoming.Menus);
        Assert.Equal(4, preview.Incoming.Commands);
        Assert.Equal(3, preview.Incoming.DirectTriggers);
        Assert.Equal(2, preview.Groups);
        Assert.Equal(1, preview.WaitingPrefixes); // "dir" waits for a possible "dir1"
        Assert.Equal(2, preview.RichTextAsPlain);
        Assert.Equal(1, preview.DuplicateAbbreviations);
        Assert.False(preview.ReplacesExisting);
    }

    [Fact]
    public void Of_DiffsAgainstTheCurrentLibrary_BySnippetId()
    {
        var current = Root(Group("g-t", "T1", Snippet("s-keep", "kk"), Snippet("s-edit", "ee"), Snippet("s-gone", "gg")));
        var incoming = Root(Group("g-t", "T1", Snippet("s-keep", "kk"), Snippet("s-edit", "ee", content: "nuevo"), Snippet("s-new", "nn")));

        var preview = ImportPreview.Of(new ATextImport(incoming, []), current);

        Assert.True(preview.ReplacesExisting);
        Assert.Equal(1, preview.Added);
        Assert.Equal(1, preview.Removed);
        Assert.Equal(1, preview.Changed);
    }

    [Fact]
    public void Of_CountsAbbreviationOrModeChangesAsChanges()
    {
        var current = Root(Group("g", null, Snippet("s-1", "aa")));
        var incoming = Root(Group("g", null, Snippet("s-1", "aa") with { Abbreviations = ["aa", "a2"] }));

        Assert.Equal(1, ImportPreview.Of(new ATextImport(incoming, []), current).Changed);
    }

    [Fact]
    public void Of_IdenticalLibrary_HasNoDifferences()
    {
        var library = Root(Group("g", "G1", Snippet("s-1", "aa")));

        var preview = ImportPreview.Of(new ATextImport(library, []), library);

        Assert.Equal((0, 0, 0), (preview.Added, preview.Removed, preview.Changed));
    }
}
