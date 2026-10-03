using TextFlow.Core.Feedback;

namespace TextFlow.Core.Tests.Feedback;

public sealed class TrayIconArtTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    public void Render_ReturnsOnePixelPerCell(int size)
    {
        Assert.Equal(size * size, TrayIconArt.Render(size, paused: false).Length);
    }

    [Fact]
    public void Render_HasTransparentRoundedCorners_AndOpaqueBody()
    {
        var pixels = TrayIconArt.Render(32, paused: false);

        Assert.Equal(0u, Alpha(pixels[0]));
        Assert.Equal(255u, Alpha(pixels[(4 * 32) + 16]));
    }

    [Fact]
    public void Paused_IsGray_ActiveIsColored()
    {
        var active = TrayIconArt.Render(32, paused: false);
        var paused = TrayIconArt.Render(32, paused: true);

        Assert.NotEqual(active, paused);
        Assert.All(paused.Where(p => Alpha(p) == 255), p => Assert.True(Spread(p) <= 8, $"pixel {p:X8} is not gray"));
        Assert.Contains(active, p => Alpha(p) == 255 && Spread(p) > 60);
    }

    private static uint Alpha(uint argb) => argb >> 24;

    private static int Spread(uint argb)
    {
        int r = (int)((argb >> 16) & 0xFF), g = (int)((argb >> 8) & 0xFF), b = (int)(argb & 0xFF);
        return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
    }
}
