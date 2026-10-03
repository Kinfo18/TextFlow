using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Library;

public sealed class AbbreviationAdvisorTests
{
    private static LibrarySnippet Snippet(string id, params string[] abbreviations) => new(id, abbreviations[0], "x", false, abbreviations);

    private static readonly LibraryGroup Root = new("root", "Biblioteca", null, true,
        [
            new LibraryGroup("g-lc", "Local cerrado", "LC", true, [], [Snippet("s-nc", "nc")]),
            new LibraryGroup("g-t", "Temples", "T1", true, [], [Snippet("s-dir", "dir"), Snippet("s-cc", "cc")]),
            new LibraryGroup("g-exact", "Exacto", "EX", false, [], [Snippet("s-acc", "Accept")]),
        ],
        []);

    private static IReadOnlyList<AbbreviationWarning> Check(string abbreviation, string groupId = "g-t", string? snippetId = "s-new") =>
        AbbreviationAdvisor.ForSnippet(Root, snippetId, groupId, [abbreviation], SnippetMode.Immediate);

    [Fact]
    public void SameAsAnotherCommand_IsReported_IgnoringCaseWhenTheGroupDoes()
    {
        var warning = Assert.Single(Check("CC"));

        Assert.Equal(AbbreviationWarningKind.UsedByCommand, warning.Kind);
        Assert.Equal("cc", warning.OtherAbbreviation);
    }

    [Fact]
    public void SameAsAGroupMenu_IsReported_TheMenuWins()
    {
        var warning = Assert.Single(Check("lc"));

        Assert.Equal(AbbreviationWarningKind.UsedByMenu, warning.Kind);
        Assert.Equal("Local cerrado", warning.Other);
    }

    [Fact]
    public void ExactCaseGroups_OnlyClashOnTheExactCase()
    {
        Assert.Empty(Check("accept", groupId: "g-exact"));
        Assert.Contains(Check("Accept", groupId: "g-exact"), w => w.Kind == AbbreviationWarningKind.UsedByCommand);
    }

    [Fact]
    public void PrefixOfALongerOne_Waits()
    {
        var warning = Assert.Single(Check("di"));

        Assert.Equal(AbbreviationWarningKind.WaitsForLonger, warning.Kind);
        Assert.Equal("dir", warning.OtherAbbreviation);
    }

    [Fact]
    public void LongerThanAnExistingOne_MakesTheShorterWait()
    {
        var warning = Assert.Single(Check("dir1"));

        Assert.Equal(AbbreviationWarningKind.MakesShorterWait, warning.Kind);
        Assert.Equal("dir", warning.OtherAbbreviation);
    }

    [Fact]
    public void TheCommandBeingEdited_DoesNotClashWithItself()
    {
        Assert.Empty(AbbreviationAdvisor.ForSnippet(Root, "s-cc", "g-t", ["cc"], SnippetMode.Immediate));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("Para")]
    [InlineData("the")]
    public void CommonWords_AreReported(string word)
    {
        Assert.Contains(Check(word, groupId: "g-t"), w => w.Kind == AbbreviationWarningKind.CommonWord);
    }

    [Fact]
    public void AfterDelimiterCommands_NeitherWaitNorFireInsideWords()
    {
        var warnings = AbbreviationAdvisor.ForSnippet(Root, "s-new", "g-t", ["di", "de"], SnippetMode.AfterDelimiter);

        Assert.Empty(warnings);
    }

    [Fact]
    public void GroupAbbreviation_IsCheckedAgainstOtherMenusAndCommands_ButNotItself()
    {
        Assert.Contains(AbbreviationAdvisor.ForGroup(Root, "g-new", "CC", ignoreCase: true), w => w.Kind == AbbreviationWarningKind.UsedByCommand);
        Assert.Contains(AbbreviationAdvisor.ForGroup(Root, "g-new", "t1", ignoreCase: true), w => w.Kind == AbbreviationWarningKind.UsedByMenu);
        Assert.Empty(AbbreviationAdvisor.ForGroup(Root, "g-lc", "LC", ignoreCase: true));
    }

    [Fact]
    public void FreeAbbreviation_HasNoWarnings()
    {
        Assert.Empty(Check("zq9"));
    }
}
