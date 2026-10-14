using TextFlow.Core.Windowing;

namespace TextFlow.Core.Tests.Windowing;

public sealed class WindowBoundsTests
{
    private static readonly WindowRect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void AWindowInsideTheScreen_KeepsItsPlaceAndSize()
    {
        Assert.Equal(new WindowRect(200, 100, 1100, 720), WindowBounds.Fit(new WindowRect(200, 100, 1100, 720), WorkArea));
    }

    [Fact]
    public void AWindowPartlyOffTheScreen_IsPulledBackInside()
    {
        Assert.Equal(new WindowRect(820, 320, 1100, 720), WindowBounds.Fit(new WindowRect(1500, 900, 1100, 720), WorkArea));
        Assert.Equal(new WindowRect(0, 0, 1100, 720), WindowBounds.Fit(new WindowRect(-300, -50, 1100, 720), WorkArea));
    }

    [Fact]
    public void AWindowLargerThanTheScreen_ShrinksToIt()
    {
        Assert.Equal(WorkArea, WindowBounds.Fit(new WindowRect(-10, -10, 2560, 1400), WorkArea));
    }

    [Fact]
    public void ASecondMonitorOnTheLeft_UsesItsOwnCoordinates()
    {
        var left = new WindowRect(-1920, 0, 1920, 1040);

        Assert.Equal(new WindowRect(-1700, 50, 1000, 700), WindowBounds.Fit(new WindowRect(-1700, 50, 1000, 700), left));
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(1100, 0)]
    [InlineData(-5, 720)]
    public void AnEmptySavedSize_GivesNothingToRestore(int width, int height)
    {
        Assert.Null(WindowBounds.Fit(new WindowRect(100, 100, width, height), WorkArea));
    }
}
