using TextFlow.Core.Expansion;
using TextFlow.Core.Library;
using TextFlow.Core.Menus;

namespace TextFlow.Core.Tests.Library;

public sealed class GroupDraftTests
{
    [Fact]
    public void New_GetsAFreshIdUnderItsParent()
    {
        var draft = GroupDraft.New("g-parent");

        Assert.StartsWith("tf-g-", draft.Id, StringComparison.Ordinal);
        Assert.NotEqual(draft.Id, GroupDraft.New("g-parent").Id);
        Assert.Equal("g-parent", draft.ParentId);
        Assert.True(draft.IgnoreCase); // aText default
    }

    [Fact]
    public void From_ThenToGroupInfo_RoundTrips()
    {
        var group = new LibraryGroup("g-ng", "No gestionable", "NG", false, [], []);

        var (info, errors) = GroupDraft.From(group, "g-reg").ToGroupInfo();

        Assert.Empty(errors);
        Assert.Equal(new GroupInfo("g-ng", "g-reg", "No gestionable", "NG", false), info);
    }

    [Fact]
    public void NameIsRequired_AbbreviationIsTrimmedAndOptional()
    {
        var (_, errors) = (GroupDraft.New("root") with { Name = "  " }).ToGroupInfo();
        Assert.Contains(GroupDraftError.NameRequired, errors);

        var (info, _) = (GroupDraft.New("root") with { Name = " Temples ", AbbreviationText = "   " }).ToGroupInfo();
        Assert.Equal("Temples", info!.Name);
        Assert.Null(info.Abbreviation);
    }

    [Fact]
    public void TooLongAbbreviation_IsAnError()
    {
        var (_, errors) = (GroupDraft.New("root") with { Name = "x", AbbreviationText = new string('a', 64) }).ToGroupInfo();

        Assert.Contains(GroupDraftError.AbbreviationTooLong, errors);
    }

    [Fact]
    public void IgnoreCaseOff_StopsTheLowercaseAbbreviationFromOpeningTheMenu()
    {
        // H3.4 acceptance: "ng" no longer opens "No gestionable" once its group stops ignoring case.
        var off = new LibraryGroup("g-ng", "No gestionable", "NG", IgnoreCase: false, [], []);
        var menu = Assert.Single(LibraryIndex.Build(new LibraryGroup("root", "root", null, true, [off], [])).Triggers);
        var matcher = new TriggerMatcher([menu], TriggerOptions.Default);

        Assert.Null(matcher.OnCharacter('n'));
        Assert.Null(matcher.OnCharacter('g'));
        matcher.Reset();
        Assert.Null(matcher.OnCharacter('N'));
        Assert.NotNull(matcher.OnCharacter('G'));
    }
}
