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
        var (_, errors) = (SnippetDraft.New() with { AbbreviationsText = new string('a', 64), Content = "x" }).ToSnippet();

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
}
