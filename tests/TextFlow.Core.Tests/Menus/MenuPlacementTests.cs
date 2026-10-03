using TextFlow.Contracts.Targeting;
using TextFlow.Core.Menus;

namespace TextFlow.Core.Tests.Menus;

public sealed class MenuPlacementTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void Place_PutsMenuBelowTheCaret()
    {
        var position = MenuPlacement.Place(new PixelRect(100, 200, 101, 220), Monitor, width: 300, height: 400, gap: 4);

        Assert.Equal((100, 224), position);
    }

    [Fact]
    public void Place_FlipsAbove_WhenThereIsNoRoomBelow()
    {
        var position = MenuPlacement.Place(new PixelRect(100, 900, 101, 920), Monitor, width: 300, height: 400, gap: 4);

        Assert.Equal((100, 496), position);
    }

    [Fact]
    public void Place_ClampsToTheRightEdge()
    {
        var position = MenuPlacement.Place(new PixelRect(1800, 200, 1801, 220), Monitor, width: 300, height: 400, gap: 4);

        Assert.Equal(1620, position.X);
    }

    [Fact]
    public void Place_StaysOnASecondaryMonitorWithNegativeCoordinates()
    {
        var left = new PixelRect(-1920, 0, 0, 1080);

        var position = MenuPlacement.Place(new PixelRect(-50, 1000, -49, 1020), left, width: 300, height: 400, gap: 4);

        Assert.Equal((-300, 596), position);
    }

    [Fact]
    public void Place_TallerThanTheMonitor_StartsAtTheTop()
    {
        var position = MenuPlacement.Place(new PixelRect(100, 500, 101, 520), Monitor, width: 300, height: 2000, gap: 4);

        Assert.Equal(0, position.Y);
    }
}
