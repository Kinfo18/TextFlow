using System.Text.Json;
using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Library;

public sealed class LibraryJsonTests
{
    private static LibraryGroup Sample() => new("root", "Biblioteca", null, true,
        [
            new LibraryGroup("g-lc", "Local cerrado", "LC", true,
                [new LibraryGroup("g-sub", "Sub", null, false, [], [new LibrarySnippet("s-note", "Nota", string.Empty, false, ["Nota informativa"])])],
                [new LibrarySnippet("s-nc", "No confirmado", "Hola 👋\r\nlínea 2 — ñandú «comillas» \"dobles\" \\ barra", true, ["nc", "No confirmado"])]),
            new LibraryGroup("g-t", "Temples", "T1", true, [],
                [new LibrarySnippet("s-sig", "sig", "firma", false, ["sig"], SnippetMode.AfterDelimiter, Enabled: false)]),
        ],
        [new LibrarySnippet("s-root", "rr", "en la raíz", false, ["rr"], SendEnter: true)]);

    private static void AssertSameTree(LibraryGroup expected, LibraryGroup actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    [Fact]
    public void ExportThenImport_IsLossless()
    {
        var json = LibraryJson.Export(Sample());

        AssertSameTree(Sample(), LibraryJson.Import(json));
    }

    [Fact]
    public void Export_IsVersionedAndReadable()
    {
        using var document = JsonDocument.Parse(LibraryJson.Export(Sample()));

        Assert.Equal("textflow-library", document.RootElement.GetProperty("format").GetString());
        Assert.Equal(LibraryJson.FormatVersion, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("after_delimiter", document.RootElement.GetProperty("root").GetProperty("groups")[1]
            .GetProperty("snippets")[0].GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"format":"something-else","version":1,"root":{}}""")]
    [InlineData("""{"format":"textflow-library","version":99,"root":{"id":"r","name":"r","groups":[],"snippets":[]}}""")]
    [InlineData("""{"format":"textflow-library","version":1}""")]
    [InlineData("""{"format":"textflow-library","version":1,"root":{"id":"","name":"r","groups":[],"snippets":[]}}""")]
    [InlineData("""{"format":"textflow-library","version":1,"root":{"id":"r","name":"r","groups":[{"id":"r","name":"dup","groups":[],"snippets":[]}],"snippets":[]}}""")]
    [InlineData("""{"format":"textflow-library","version":1,"root":{"id":"r","name":"r","groups":[],"snippets":[{"id":"s","name":"s","content":"x","abbreviations":["a"],"mode":"sometimes"}]}}""")]
    public void Import_RejectsInvalidFiles_WithInvalidData(string json)
    {
        Assert.Throws<InvalidDataException>(() => LibraryJson.Import(json));
    }

    [Fact]
    public void Import_RejectsAbsurdNesting()
    {
        var deep = new LibraryGroup("g-0", "0", null, true, [], []);
        for (var i = 1; i <= LibraryJson.MaxDepth + 5; i++)
        {
            deep = new LibraryGroup($"g-{i}", $"{i}", null, true, [deep], []);
        }

        Assert.Throws<InvalidDataException>(() => LibraryJson.Import(LibraryJson.Export(deep)));
    }

    [Fact]
    public void Import_FillsOptionalFieldsWithDefaults()
    {
        const string json = """
            {"format":"textflow-library","version":1,"root":{"id":"r","name":"r","groups":[],
             "snippets":[{"id":"s","name":"s","content":"x","abbreviations":["aa"]}]}}
            """;

        var snippet = Assert.Single(LibraryJson.Import(json).Snippets);

        Assert.Equal(SnippetMode.Immediate, snippet.Mode);
        Assert.True(snippet.Enabled);
        Assert.False(snippet.IsRichText);
    }
}
