using TextFlow.Core.Import;
using TextFlow.Core.Menus;
using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Menus;

public sealed class LibrarySummaryTests
{
    private static LibrarySnippet Snippet(string abbreviation, string content = "x") =>
        new($"s-{abbreviation}", abbreviation, content, IsRichText: false, [abbreviation]);

    [Fact]
    public void Of_CountsMenusCommandsDirectTriggersAndIssues()
    {
        var root = new LibraryGroup("root", "root", null, true,
            [
                new LibraryGroup("g1", "Local cerrado", "LC", true,
                    [new LibraryGroup("g1a", "Sub", null, true, [], [Snippet("Nota informativa", content: "")])],
                    [Snippet("nc1"), Snippet("Frase con espacios larga")]),
                new LibraryGroup("g2", "Temples", null, true, [], [Snippet("cc"), Snippet("dir1")]),
            ],
            []);
        var import = new ATextImport(root, [new ImportIssue(ImportIssueCode.RichTextImportedAsPlain, "s-cc", "cc")]);

        var summary = LibrarySummary.Of(import, LibraryIndex.Build(root));

        Assert.Equal(1, summary.Menus);
        Assert.Equal(5, summary.Commands);           // every snippet, info notes included (aText's count)
        Assert.Equal(4, summary.DirectTriggers);     // nc1, the phrase with spaces, cc, dir1 (the note inserts nothing)
        Assert.Equal(1, summary.Issues);
    }
}
