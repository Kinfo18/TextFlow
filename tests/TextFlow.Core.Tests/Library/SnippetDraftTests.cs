using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Library;

public sealed class SnippetDraftTests
{
    [Fact]
    public void FromSnippet_ThenToSnippet_RoundTrips()
    {
        var snippet = new LibrarySnippet("s-1", "Saludo", "Hola\r\nqué tal", true, ["hh", "h2"], SnippetMode.AfterDelimiter, Enabled: false);

        var draft = SnippetDraft.From(snippet);

        Assert.Equal("hh\nh2", draft.AbbreviationsText);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(snippet), System.Text.Json.JsonSerializer.Serialize(draft.ToSnippet().Snippet));
    }

    [Fact]
    public void Abbreviations_AreOnePerLine_TrimmedAndDeduplicated()
    {
        var draft = SnippetDraft.New() with { AbbreviationsText = "  cc \r\n\ncc\nFoto valida  \n", Content = "x" };

        var (snippet, errors) = draft.ToSnippet();

        Assert.Empty(errors);
        Assert.Equal(["cc", "Foto valida"], snippet!.Abbreviations);
    }

    [Fact]
    public void Name_DefaultsToTheFirstAbbreviation()
    {
        var (snippet, _) = (SnippetDraft.New() with { AbbreviationsText = "cc", Name = "  ", Content = "x" }).ToSnippet();

        Assert.Equal("cc", snippet!.Name);
    }

    [Fact]
    public void NewDraft_GetsAFreshTextFlowId()
    {
        Assert.NotEqual(SnippetDraft.New().Id, SnippetDraft.New().Id);
        Assert.StartsWith("tf-", SnippetDraft.New().Id, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAbbreviationAndNoName_IsAnError()
    {
        var (snippet, errors) = (SnippetDraft.New() with { Content = "texto" }).ToSnippet();

        Assert.Null(snippet);
        Assert.Contains(SnippetDraftError.NeedsAbbreviationOrName, errors);
    }

    [Fact]
    public void TooLongAbbreviation_IsAnError()
    {
        var (_, errors) = (SnippetDraft.New() with { AbbreviationsText = new string('a', SnippetDraft.MaxAbbreviationLength + 1), Content = "x" }).ToSnippet();

        Assert.Contains(SnippetDraftError.AbbreviationTooLong, errors);
    }

    [Fact]
    public void AfterDelimiterAbbreviation_CannotContainASpace()
    {
        var (_, errors) = (SnippetDraft.New() with { AbbreviationsText = "foto valida", Mode = SnippetMode.AfterDelimiter, Content = "x" }).ToSnippet();

        Assert.Contains(SnippetDraftError.DelimiterInAbbreviation, errors);
    }

    [Fact]
    public void EmptyContent_IsAllowed_AsAnInfoNote()
    {
        var (snippet, errors) = (SnippetDraft.New() with { Name = "Nota para el menú" }).ToSnippet();

        Assert.Empty(errors);
        Assert.True(snippet!.IsInfoOnly);
    }

    [Fact]
    public void ContentLineEndings_AreKeptAsTyped()
    {
        var (snippet, _) = (SnippetDraft.New() with { AbbreviationsText = "cc", Content = "a\rb" }).ToSnippet();

        Assert.Equal("a\rb", snippet!.Content);
    }

    [Fact]
    public void HasSameEdits_IgnoresHowTheEditorWritesLineBreaks()
    {
        var loaded = SnippetDraft.From(new LibrarySnippet("s-1", "Saludo", "Hola\r\nqué tal", false, ["hh", "h2"]));

        // A WinUI TextBox gives "\r" line breaks back for both boxes.
        var shown = loaded with { AbbreviationsText = "hh\rh2", Content = "Hola\rqué tal" };

        Assert.True(shown.HasSameEdits(loaded));
    }

    [Theory]
    [InlineData("Saludo", "Hola\nqué tal", true)]
    [InlineData("Saludo 2", "Hola\nqué tal", false)]
    [InlineData("Saludo", "Hola\nqué tal!", false)]
    public void HasSameEdits_SeesRealChanges(string name, string content, bool same)
    {
        var loaded = SnippetDraft.From(new LibrarySnippet("s-1", "Saludo", "Hola\nqué tal", false, ["hh"]));

        Assert.Equal(same, (loaded with { Name = name, Content = content }).HasSameEdits(loaded));
        Assert.False((loaded with { Enabled = false }).HasSameEdits(loaded));
        Assert.False((loaded with { Mode = SnippetMode.AfterDelimiter }).HasSameEdits(loaded));
    }
}
