using TextFlow.Core.Expansion;
using TextFlow.Core.Import;
using TextFlow.Core.Menus;

namespace TextFlow.Core.Tests.Menus;

public class GroupMenuIndexTests
{
    private static ImportedSnippet Snippet(string name, string content = "x") => new($"s-{name}", name, content, IsRichText: false);

    private static ImportedGroup Group(
        string name, string? abbreviation, bool ignoreCase = true, ImportedGroup[]? groups = null, ImportedSnippet[]? snippets = null) =>
        new($"g-{name}", name, abbreviation, ignoreCase, groups ?? [], snippets ?? []);

    private static ImportedGroup Library() => Group("root", null, groups:
    [
        Group("Local Cerrado", "LC", snippets: [Snippet("No confirmado"), Snippet("Nota", content: "")]),
        Group("Orden demorada", "OD", snippets: [Snippet("Demora")]),
        Group("CROSS", "CROSS", groups: [Group("Reasignación", "REG", groups: [Group("No gestionable", "NG", ignoreCase: false)])]),
        Group("Orden dañada", "od", snippets: [Snippet("Daño")]),
        Group("Sin abreviatura", null, snippets: [Snippet("Suelto")]),
    ]);

    [Fact]
    public void Build_CreatesOneMenuPerAbbreviation_IncludingNestedGroups()
    {
        var index = GroupMenuIndex.Build(Library());

        Assert.Equal(["LC", "OD", "CROSS", "REG", "NG"], index.Menus.Select(m => m.Trigger));
    }

    [Fact]
    public void Build_SingleGroupMenu_ListsItsSubgroupsThenSnippets()
    {
        var cross = GroupMenuIndex.Build(Library()).Menus.Single(m => m.Trigger == "CROSS");

        var reg = Assert.IsType<MenuGroupEntry>(Assert.Single(cross.Entries));
        Assert.Equal("Reasignación", reg.Label);
    }

    [Fact]
    public void Build_DuplicateAbbreviations_IgnoringCase_MergeIntoOneMenuListingEachGroup()
    {
        var od = GroupMenuIndex.Build(Library()).Menus.Single(m => m.Trigger == "OD");

        Assert.Equal(["Orden demorada", "Orden dañada"], od.Entries.Select(e => e.Label));
        Assert.All(od.Entries, e => Assert.IsType<MenuGroupEntry>(e));
    }

    [Fact]
    public void Build_KeepsPerGroupCaseSetting()
    {
        var index = GroupMenuIndex.Build(Library());

        Assert.True(index.Menus.Single(m => m.Trigger == "LC").IgnoreCase);
        Assert.False(index.Menus.Single(m => m.Trigger == "NG").IgnoreCase);
    }

    [Fact]
    public void Build_ExactCaseGroups_WithDifferentCasing_AreSeparateMenus()
    {
        var root = Group("root", null, groups: [Group("A", "ab", ignoreCase: false), Group("B", "AB", ignoreCase: false)]);

        Assert.Equal(2, GroupMenuIndex.Build(root).Menus.Count);
    }

    [Fact]
    public void Build_EmptySnippets_BecomeInfoOnlyEntries()
    {
        var lc = GroupMenuIndex.Build(Library()).Menus.Single(m => m.Trigger == "LC");

        var note = Assert.IsType<MenuSnippetEntry>(lc.Entries[1]);
        Assert.True(note.IsInfoOnly);
        Assert.False(note.IsSelectable);
    }

    [Fact]
    public void Triggers_AreImmediate_AndCarryCaseSetting()
    {
        var index = GroupMenuIndex.Build(Library());

        Assert.All(index.Triggers, t => Assert.Equal(TriggerMode.Immediate, t.Mode));
        Assert.False(index.Triggers.Single(t => t.Trigger == "NG").IgnoreCase);
    }

    [Fact]
    public void Find_ReturnsMenuForTriggerId()
    {
        var index = GroupMenuIndex.Build(Library());
        var trigger = index.Triggers.Single(t => t.Trigger == "LC");

        Assert.Equal("LC", index.Find(trigger.SnippetId)?.Trigger);
        Assert.Null(index.Find("unknown"));
    }
}
