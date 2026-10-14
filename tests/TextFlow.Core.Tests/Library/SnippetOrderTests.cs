using TextFlow.Core.Library;

namespace TextFlow.Core.Tests.Library;

public sealed class SnippetOrderTests
{
    private static readonly string[] Ids = ["a", "b", "c", "d"];

    [Theory]
    [InlineData("c", OrderMove.Up, "a,c,b,d")]
    [InlineData("b", OrderMove.Down, "a,c,b,d")]
    [InlineData("c", OrderMove.First, "c,a,b,d")]
    [InlineData("b", OrderMove.Last, "a,c,d,b")]
    public void Move_PutsTheCommandInItsNewPlace(string id, OrderMove move, string expected)
    {
        Assert.Equal(expected.Split(','), SnippetOrder.Move(Ids, id, move));
    }

    [Theory]
    [InlineData("a", OrderMove.Up)]
    [InlineData("a", OrderMove.First)]
    [InlineData("d", OrderMove.Down)]
    [InlineData("d", OrderMove.Last)]
    [InlineData("zz", OrderMove.Up)]
    public void Move_AtTheEndOrUnknown_ReturnsNull(string id, OrderMove move)
    {
        Assert.Null(SnippetOrder.Move(Ids, id, move));
    }

    [Fact]
    public void Move_LeavesTheInputUntouched()
    {
        var ids = new[] { "a", "b" };

        SnippetOrder.Move(ids, "b", OrderMove.Up);

        Assert.Equal(["a", "b"], ids);
    }
}
