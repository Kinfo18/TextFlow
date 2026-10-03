using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Library;

public sealed class LibrarySearchTests
{
    private static LibrarySnippet Snippet(string id, string content, params string[] abbreviations) =>
        new(id, abbreviations.FirstOrDefault() ?? id, content, false, abbreviations);

    private static readonly LibraryGroup Root = new("root", "Biblioteca", null, true,
        [
            new LibraryGroup("g-orca", "Orcas", "OR", true,
                [new LibraryGroup("g-deep", "Profundo", null, true, [], [Snippet("s-orca3", "Ballena asesina", "orca3")])],
                [Snippet("s-orca", "texto orca", "orca"), Snippet("s-orca10", "diez", "orca10")]),
            new LibraryGroup("g-t", "Temples", "T1", true, [],
                [Snippet("s-cc", "Hola, ¿cómo estás? Llamé a la ORCA", "cc"), Snippet("s-cafe", "café", "Canción")]),
        ],
        []);

    [Fact]
    public void ExactAbbreviation_ComesFirst_ThenPrefixes_ThenNames_ThenContent()
    {
        var results = LibrarySearch.Find(Root, "orca");

        Assert.Equal(["s-orca", "s-orca10", "s-orca3", "s-cc"], results.Select(r => r.Snippet.Id));
    }

    [Fact]
    public void FindsOrca3_WithItsGroupPath()
    {
        var hit = LibrarySearch.Find(Root, "orca3")[0];

        Assert.Equal("s-orca3", hit.Snippet.Id);
        Assert.Equal(["Orcas", "Profundo"], hit.GroupPath);
        Assert.Equal("g-deep", hit.GroupId);
    }

    [Theory]
    [InlineData("CANCION")]  // case and accents ignored
    [InlineData("cancion")]
    [InlineData("Canción")]
    public void IgnoresCaseAndAccents(string query)
    {
        Assert.Equal("s-cafe", Assert.Single(LibrarySearch.Find(Root, query)).Snippet.Id);
    }

    [Fact]
    public void SearchesContent()
    {
        Assert.Equal("s-cc", Assert.Single(LibrarySearch.Find(Root, "como estas")).Snippet.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQuery_FindsNothing(string query)
    {
        Assert.Empty(LibrarySearch.Find(Root, query));
    }

    [Fact]
    public void LargeLibrary_IsSearchedWellUnderASecond()
    {
        var groups = Enumerable.Range(0, 50).Select(g => new LibraryGroup($"g{g}", $"Grupo {g}", null, true, [],
            [.. Enumerable.Range(0, 200).Select(s => Snippet($"s{g}-{s}", $"contenido largo número {s} del grupo {g} con texto", $"ab{g}x{s}"))])).ToArray();
        var big = new LibraryGroup("root", "root", null, true, groups, []);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var results = LibrarySearch.Find(big, "ab7x199");

        Assert.Equal("s7-199", results[0].Snippet.Id);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"took {clock.ElapsedMilliseconds} ms");
    }
}
