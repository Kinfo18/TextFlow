using TextFlow.Contracts.Targeting;

namespace TextFlow.Core.Menus;

/// <summary>Where the group menu goes, in physical pixels: below the caret, above it when there is no room, always on the target's monitor.</summary>
public static class MenuPlacement
{
    public static (int X, int Y) Place(PixelRect anchor, PixelRect monitor, int width, int height, int gap)
    {
        var x = Math.Clamp(anchor.Left, monitor.Left, Math.Max(monitor.Left, monitor.Right - width));
        var y = anchor.Bottom + gap;
        if (y + height > monitor.Bottom)
        {
            y = Math.Max(monitor.Top, anchor.Top - gap - height);
        }

        return (x, y);
    }
}
