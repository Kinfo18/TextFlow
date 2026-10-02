using System.Text;
using K4os.Compression.LZ4.Streams;
using TextFlow.Core.Import;

namespace TextFlow.Core.Tests.Import;

public class ATextBackupReaderTests
{
    private const string Header = """{"0":"00000000-0000-0000-0000-000000000000","1":true}""";

    // Shape observed in a real aText for Windows backup (2026-10-02); content is synthetic.
    private const string Library = """
        [{"99":1,"0":"root","2":"Xtendo","6":2,"12":1,"13":[
          {"99":1,"0":"g-lc","2":"Local Cerrado","8":1,"12":1,"14":"LC","13":[
            {"0":"s-1","1":["No confirmado"],"3":"t","4":"Pedido no está confirmado","13":1},
            {"0":"s-2","1":["Con salto"],"3":"h","4":"línea 1\nlínea 2","13":1}
          ]},
          {"99":1,"0":"g-finp","2":"Finalización","12":1,"14":"finp","13":[
            {"99":1,"0":"g-sub","2":"Sin abreviatura","12":1,"13":[
              {"0":"s-3","1":[],"3":"t","4":"sin nombre","13":1}
            ]}
          ]},
          {"99":1,"0":"g-od2","2":"Orden dañada","8":1,"12":1,"14":"od","13":[
            {"0":"s-4","1":["Vacío"],"3":"t","13":1}
          ]},
          {"99":1,"0":"g-od1","2":"Orden demorada","8":1,"12":1,"14":"OD","13":[]}
        ]}]
        """;

    private static byte[] Backup(string library)
    {
        using var output = new MemoryStream();
        output.Write(Encoding.UTF8.GetPreamble());
        output.Write(Encoding.UTF8.GetBytes(Header));
        output.WriteByte(0);
        using (var lz4 = LZ4Stream.Encode(output, leaveOpen: true))
        {
            lz4.Write(Encoding.UTF8.GetBytes(library));
        }

        return output.ToArray();
    }

    private static ATextImport Read(string library = Library) => ATextBackupReader.Read(new MemoryStream(Backup(library)));

    private static ImportedGroup Group(ATextImport import, string id) => Flatten(import.Root).Single(g => g.Id == id);

    private static IEnumerable<ImportedGroup> Flatten(ImportedGroup group) => group.Groups.SelectMany(Flatten).Prepend(group);

    [Fact]
    public void Read_ReturnsGroupTree_WithNamesAndAbbreviations()
    {
        var import = Read();

        Assert.Equal("Xtendo", import.Root.Name);
        Assert.Equal(4, import.Root.Groups.Count);
        Assert.Equal("LC", Group(import, "g-lc").Abbreviation);
        Assert.Null(Group(import, "g-sub").Abbreviation);
    }

    [Fact]
    public void Read_IgnoresCaseByDefault_RegardlessOfFlag8()
    {
        // The user confirmed "ng" opens a group stored without key 8: case is a global aText setting.
        var import = Read();

        Assert.True(Group(import, "g-lc").IgnoreCase);
        Assert.True(Group(import, "g-finp").IgnoreCase);
    }

    [Fact]
    public void Read_CanImportAsCaseSensitive()
    {
        var import = ATextBackupReader.Read(new MemoryStream(Backup(Library)), ignoreCase: false);

        Assert.False(Group(import, "g-lc").IgnoreCase);
    }

    [Fact]
    public void Read_ReturnsSnippets_WithNameAndUnicodeContent()
    {
        var snippet = Group(Read(), "g-lc").Snippets[0];

        Assert.Equal("s-1", snippet.Id);
        Assert.Equal("No confirmado", snippet.Name);
        Assert.Equal("Pedido no está confirmado", snippet.Content);
        Assert.False(snippet.IsRichText);
    }

    [Fact]
    public void Read_KeepsNewlines_AndFlagsRichText()
    {
        var snippet = Group(Read(), "g-lc").Snippets[1];

        Assert.Equal("línea 1\nlínea 2", snippet.Content);
        Assert.True(snippet.IsRichText);
        Assert.Contains(Read().Issues, i => i is { Code: ImportIssueCode.RichTextImportedAsPlain, ItemId: "s-2" });
    }

    [Fact]
    public void Read_SnippetWithoutName_UsesContentPreview_AndReportsIt()
    {
        var import = Read();
        var snippet = Group(import, "g-sub").Snippets[0];

        Assert.Equal("sin nombre", snippet.Name);
        Assert.Contains(import.Issues, i => i is { Code: ImportIssueCode.MissingName, ItemId: "s-3" });
    }

    [Fact]
    public void Read_SnippetWithoutContent_IsInfoOnly_AndNotAnIssue()
    {
        // The user keeps empty snippets as read-only notes shown in the group menu.
        var import = Read();
        var snippet = Group(import, "g-od2").Snippets[0];

        Assert.Equal(string.Empty, snippet.Content);
        Assert.True(snippet.IsInfoOnly);
        Assert.DoesNotContain(import.Issues, i => i.ItemId == "s-4");
    }

    [Fact]
    public void Read_ReportsAbbreviationsThatCollideIgnoringCase()
    {
        var import = Read();

        Assert.Contains(import.Issues, i => i is { Code: ImportIssueCode.DuplicateAbbreviation, ItemId: "g-od1" });
    }

    [Fact]
    public void Read_ReportsUnknownFields_SoFormatChangesAreNoticed()
    {
        var import = Read("""[{"99":1,"0":"root","2":"R","12":1,"13":[{"0":"s","1":["n"],"3":"t","4":"x","13":1,"77":"?"}]}]""");

        Assert.Contains(import.Issues, i => i is { Code: ImportIssueCode.UnknownField, ItemId: "s" });
    }

    [Fact]
    public void Read_IssuesNeverContainSnippetContent()
    {
        var import = Read();

        Assert.DoesNotContain(import.Issues, i => i.Detail.Contains("Pedido", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_RejectsFileWithoutLz4Payload()
    {
        var bytes = Encoding.UTF8.GetBytes(Header + "\0not lz4");

        Assert.Throws<InvalidDataException>(() => ATextBackupReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void Read_RejectsFileWithoutHeaderSeparator()
    {
        Assert.Throws<InvalidDataException>(() => ATextBackupReader.Read(new MemoryStream(Encoding.UTF8.GetBytes("[]"))));
    }
}
