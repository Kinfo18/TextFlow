using TextFlow.Contracts.Targeting;
using TextFlow.Core.Menus;

namespace TextFlow.Core.Tests.Menus;

public class CaretGeometryTests
{
    private static readonly PixelRect Letter = new(100, 200, 109, 220); // 9×20 px character

    [Fact]
    public void FromSelection_UsesTheEndOfAVisibleSelection()
    {
        var caret = CaretGeometry.FromSelection([new(100, 200, 180, 220), new(20, 222, 60, 242)]);

        Assert.Equal(new PixelRect(60, 222, 61, 242), caret);
    }

    [Fact]
    public void FromSelection_EmptyRangeHasNoRectangles_SoItGivesNothing()
    {
        Assert.Null(CaretGeometry.FromSelection([]));
    }

    [Fact]
    public void FromNeighbours_PrefersTheRightEdgeOfThePreviousCharacter_TheTriggerJustTyped()
    {
        var caret = CaretGeometry.FromNeighbours(next: [new(109, 200, 118, 220)], previous: [Letter]);

        Assert.Equal(new PixelRect(109, 200, 110, 220), caret);
    }

    [Fact]
    public void FromNeighbours_AtTheEndOfALine_IgnoresTheStartOfTheNextLine()
    {
        // Chrome, caret after "texto": the "next character" is the start of the following line (2026-10-04).
        var caret = CaretGeometry.FromNeighbours(next: [new(535, 237, 545, 260)], previous: [new(808, 214, 817, 237)]);

        Assert.Equal(new PixelRect(817, 214, 818, 237), caret);
    }

    [Theory]
    [InlineData(91, 200, 91, 220)]   // zero-width newline before the caret (caret at the start of a line)
    [InlineData(91, 200, 900, 220)]  // newline drawn up to the right margin
    public void FromNeighbours_ANewlineBeforeTheCaret_UsesTheLeftEdgeOfTheNextCharacter(int l, int t, int r, int b)
    {
        var caret = CaretGeometry.FromNeighbours(next: [Letter], previous: [new(l, t, r, b)]);

        Assert.Equal(new PixelRect(100, 200, 101, 220), caret);
    }

    [Fact]
    public void FromNeighbours_AtTheStartOfTheText_UsesTheNextCharacter()
    {
        Assert.Equal(new PixelRect(100, 200, 101, 220), CaretGeometry.FromNeighbours(next: [Letter], previous: []));
    }

    [Fact]
    public void FromNeighbours_UsesAnOddPreviousCharacter_WhenThereIsNothingElse()
    {
        var caret = CaretGeometry.FromNeighbours(next: [], previous: [new(300, 200, 300, 220)]);

        Assert.Equal(new PixelRect(300, 200, 301, 220), caret);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]        // providers report "no layout" as an empty rectangle
    [InlineData(100, 200, 109, 200)] // no height
    [InlineData(100, 0, 109, 900)]  // a whole page, not a line of text
    public void ImplausibleRectangles_AreIgnored(int l, int t, int r, int b)
    {
        Assert.Null(CaretGeometry.FromNeighbours(next: [new(l, t, r, b)], previous: [new(l, t, r, b)]));
        Assert.Null(CaretGeometry.FromSelection([new(l, t, r, b)]));
    }
}
