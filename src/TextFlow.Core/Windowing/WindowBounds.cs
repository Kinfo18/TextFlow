namespace TextFlow.Core.Windowing;

/// <summary>A window or screen rectangle in physical pixels (a monitor left of the main one has negative X).</summary>
public readonly record struct WindowRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Where the main window reopens: its last place, kept fully inside the screen it lands on.</summary>
public static class WindowBounds
{
    /// <summary>
    /// The saved rectangle moved (and shrunk if needed) into <paramref name="workArea"/>, so a window last seen on a
    /// monitor that is now unplugged or smaller never opens out of reach. Null when nothing usable was saved.
    /// </summary>
    public static WindowRect? Fit(WindowRect saved, WindowRect workArea)
    {
        if (saved.IsEmpty || workArea.IsEmpty)
        {
            return null;
        }

        var width = Math.Min(saved.Width, workArea.Width);
        var height = Math.Min(saved.Height, workArea.Height);
        var x = Math.Clamp(saved.X, workArea.X, workArea.X + workArea.Width - width);
        var y = Math.Clamp(saved.Y, workArea.Y, workArea.Y + workArea.Height - height);
        return new WindowRect(x, y, width, height);
    }
}
