using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Library;

public sealed class UsageReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static LibrarySnippet Snippet(string id, bool enabled = true, string content = "x") =>
        new(id, id, content, IsRichText: false, [id], Enabled: enabled);

    private static readonly LibraryGroup Root = new("root", "root", null, true,
        [new LibraryGroup("g", "Chat", "CH", true, [], [Snippet("a"), Snippet("b"), Snippet("nota", content: "")])],
        [Snippet("c"), Snippet("off", enabled: false)]);

    [Fact]
    public void Top_OrdersByCount_ThenByMostRecent_AndSkipsDeletedSnippets()
    {
        var usage = new Dictionary<string, SnippetUsage>
        {
            ["a"] = new(5, Now.AddDays(-3)),
            ["b"] = new(9, Now.AddDays(-1)),
            ["c"] = new(5, Now.AddHours(-1)),
            ["deleted"] = new(50, Now),
        };

        var top = UsageReport.Top(Root, usage, count: 3);

        Assert.Equal(["b", "c", "a"], top.Select(t => t.Snippet.Id));
        Assert.Equal([9, 5, 5], top.Select(t => t.Uses));
        Assert.Equal("Chat", top[0].GroupName);
    }

    [Fact]
    public void Top_IsEmpty_WithoutUsage()
    {
        Assert.Empty(UsageReport.Top(Root, new Dictionary<string, SnippetUsage>(), count: 5));
    }

    [Fact]
    public void Unused_ListsEnabledSnippetsNotUsedWithinTheWindow_NotesAndDisabledExcluded()
    {
        var usage = new Dictionary<string, SnippetUsage>
        {
            ["a"] = new(1, Now.AddDays(-40)),
            ["b"] = new(3, Now.AddDays(-2)),
        };

        var unused = UsageReport.Unused(Root, usage, Now, days: 30);

        Assert.Equal(["a", "c"], unused.Select(s => s.Id).Order());
    }
}
