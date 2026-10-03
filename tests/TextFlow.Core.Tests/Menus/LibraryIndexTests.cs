using TextFlow.Core.Expansion;
using TextFlow.Core.Import;
using TextFlow.Core.Menus;
using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Menus;

public class LibraryIndexTests
{
    private static LibrarySnippet Snippet(string name, string content = "x") => new($"s-{name}", name, content, IsRichText: false, [name]);

    private static LibrarySnippet Command(string abbreviation, string content = "texto") =>
        new($"s-{abbreviation}", abbreviation, content, IsRichText: false, [abbreviation]);

    private static LibraryGroup Group(
        string name, string? abbreviation, bool ignoreCase = true, LibraryGroup[]? groups = null, LibrarySnippet[]? snippets = null) =>
        new($"g-{name}", name, abbreviation, ignoreCase, groups ?? [], snippets ?? []);

    private static LibraryGroup Library() => Group("root", null, groups:
    [
        Group("Local Cerrado", "LC", snippets: [Snippet("No confirmado"), Snippet("Nota", content: "")]),
        Group("Orden demorada", "OD", snippets: [Snippet("Demora")]),
        Group("CROSS", "CROSS", groups: [Group("Reasignación", "REG", groups: [Group("No gestionable", "NG", ignoreCase: false)])]),
        Group("Orden dañada", "od", snippets: [Snippet("Daño")]),
        Group("Sin abreviatura", null, snippets: [Snippet("Suelto")]),
        Group("Temples", "T1", snippets: [Command("cc", "mensaje cc"), Command("s1"), Command("od1")]),
        Group("Exacto", null, ignoreCase: false, snippets: [Command("Accept")]),
    ]);

    [Fact]
    public void DisabledSnippet_DoesNotExpand_ButStaysInItsMenu()
    {
        var off = Command("zz") with { Enabled = false };
        var root = Group("root", null, groups: [Group("Temples", "T1", snippets: [off])]);

        var index = LibraryIndex.Build(root);

        Assert.DoesNotContain(index.Triggers, t => t.Trigger == "zz");
        Assert.Contains(index.FindMenu(index.Menus.Single().Id)!.Entries, e => e.Label == "zz");
    }

    [Fact]
    public void AfterDelimiterSnippet_GetsAnAfterDelimiterTrigger()
    {
        var root = Group("root", null, groups: [Group("Firmas", null, snippets: [Command("sig") with { Mode = SnippetMode.AfterDelimiter }])]);

        var trigger = Assert.Single(LibraryIndex.Build(root).Triggers);

        Assert.Equal(TriggerMode.AfterDelimiter, trigger.Mode);
    }

    [Fact]
    public void Build_CreatesOneMenuPerAbbreviation_IncludingNestedGroups()
    {
        var index = LibraryIndex.Build(Library());

        Assert.Equal(["LC", "OD", "CROSS", "REG", "NG", "T1"], index.Menus.Select(m => m.Trigger));
    }

    [Fact]
    public void Build_SingleGroupMenu_ListsItsSubgroupsThenSnippets()
    {
        var cross = LibraryIndex.Build(Library()).Menus.Single(m => m.Trigger == "CROSS");

        var reg = Assert.IsType<MenuGroupEntry>(Assert.Single(cross.Entries));
        Assert.Equal("Reasignación", reg.Label);
    }

    [Fact]
    public void Build_DuplicateAbbreviations_IgnoringCase_MergeIntoOneMenuListingEachGroup()
    {
        var od = LibraryIndex.Build(Library()).Menus.Single(m => m.Trigger == "OD");

        Assert.Equal(["Orden demorada", "Orden dañada"], od.Entries.Select(e => e.Label));
        Assert.All(od.Entries, e => Assert.IsType<MenuGroupEntry>(e));
    }

    [Fact]
    public void Build_KeepsPerGroupCaseSetting()
    {
        var index = LibraryIndex.Build(Library());

        Assert.True(index.Menus.Single(m => m.Trigger == "LC").IgnoreCase);
        Assert.False(index.Menus.Single(m => m.Trigger == "NG").IgnoreCase);
    }

    [Fact]
    public void Build_ExactCaseGroups_WithDifferentCasing_AreSeparateMenus()
    {
        var root = Group("root", null, groups: [Group("A", "ab", ignoreCase: false), Group("B", "AB", ignoreCase: false)]);

        Assert.Equal(2, LibraryIndex.Build(root).Menus.Count);
    }

    [Fact]
    public void Build_EmptySnippets_BecomeInfoOnlyEntries()
    {
        var lc = LibraryIndex.Build(Library()).Menus.Single(m => m.Trigger == "LC");

        var note = Assert.IsType<MenuSnippetEntry>(lc.Entries[1]);
        Assert.True(note.IsInfoOnly);
        Assert.False(note.IsSelectable);
    }

    [Fact]
    public void Build_TypeableSnippetAbbreviations_BecomeDirectTriggers()
    {
        var index = LibraryIndex.Build(Library());
        var cc = index.Triggers.Single(t => t.Trigger == "cc");

        Assert.Equal("mensaje cc", index.FindSnippet(cc.SnippetId)?.Content);
        Assert.Null(index.FindMenu(cc.SnippetId));
        Assert.Contains(index.Triggers, t => t.Trigger == "s1");
        Assert.Contains(index.Triggers, t => t.Trigger == "No confirmado"); // spaces allowed, as in aText
    }

    [Fact]
    public void Build_SnippetTriggers_InheritGroupCaseSetting()
    {
        var index = LibraryIndex.Build(Library());

        Assert.True(index.Triggers.Single(t => t.Trigger == "cc").IgnoreCase);
        Assert.False(index.Triggers.Single(t => t.Trigger == "Accept").IgnoreCase);
    }

    [Fact]
    public void Build_SnippetTriggerColliding_WithMenuTrigger_IsSkipped_MenuWins()
    {
        var root = Group("root", null, groups: [Group("Menu", "cc", snippets: [Command("x1")]), Group("T", null, snippets: [Command("CC")])]);

        var index = LibraryIndex.Build(root);

        var trigger = Assert.Single(index.Triggers, t => string.Equals(t.Trigger, "cc", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(index.FindMenu(trigger.SnippetId));
    }

    [Fact]
    public void Build_InfoOnlySnippets_DoNotBecomeTriggers()
    {
        var root = Group("root", null, groups: [Group("T", null, snippets: [Command("nota1", content: "")])]);

        Assert.Empty(LibraryIndex.Build(root).Triggers);
    }

    [Fact]
    public void Triggers_AreImmediate_AndCarryCaseSetting()
    {
        var index = LibraryIndex.Build(Library());

        Assert.All(index.Triggers, t => Assert.Equal(TriggerMode.Immediate, t.Mode));
        Assert.False(index.Triggers.Single(t => t.Trigger == "NG").IgnoreCase);
    }

    [Fact]
    public void Find_ReturnsMenuForTriggerId()
    {
        var index = LibraryIndex.Build(Library());
        var trigger = index.Triggers.Single(t => t.Trigger == "LC");

        Assert.Equal("LC", index.FindMenu(trigger.SnippetId)?.Trigger);
        Assert.Null(index.FindMenu("unknown"));
        Assert.Null(index.FindSnippet("unknown"));
    }

    [Fact]
    public void Build_AbbreviationsLongerThanTheTypingBuffer_StayMenuLabelsOnly()
    {
        var label = new string('x', TriggerOptions.Default.MaxBufferLength);
        var root = Group("root", null, groups: [Group("G", "GG", snippets: [Command(label)])]);

        var index = LibraryIndex.Build(root);

        Assert.DoesNotContain(index.Triggers, t => t.Trigger == label);
        Assert.Equal(label, index.Menus.Single().Entries.Single().Label);
        _ = new TriggerMatcher(index.Triggers, TriggerOptions.Default); // must not throw
    }
}
