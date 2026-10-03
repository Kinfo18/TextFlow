namespace TextFlow.Core.Feedback;

/// <summary>
/// Notification-area icon drawn in code, so it is crisp at any DPI without shipping .ico files:
/// a rounded square with the app gradient (blue → violet) and three "text lines"; paused is gray with a pause sign.
/// </summary>
public static class TrayIconArt
{
    private const int Samples = 4; // 4x4 supersampling for smooth edges
    private const double CornerRadius = 0.24;

    private static readonly (double R, double G, double B) ActiveFrom = (0x3B, 0x82, 0xF6);
    private static readonly (double R, double G, double B) ActiveTo = (0x8B, 0x5C, 0xF6);
    private static readonly (double R, double G, double B) PausedFrom = (0x8E, 0x8E, 0x93);
    private static readonly (double R, double G, double B) PausedTo = (0x63, 0x63, 0x68);

    /// <summary>Text lines (left, top, right, bottom) as fractions of the icon.</summary>
    private static readonly (double L, double T, double R, double B)[] ActiveGlyph =
    [
        (0.24, 0.27, 0.76, 0.37),
        (0.24, 0.45, 0.62, 0.55),
        (0.24, 0.63, 0.70, 0.73),
    ];

    private static readonly (double L, double T, double R, double B)[] PausedGlyph =
    [
        (0.33, 0.27, 0.45, 0.73),
        (0.55, 0.27, 0.67, 0.73),
    ];

    /// <returns>Row-major, top-down, straight (non-premultiplied) 0xAARRGGBB pixels.</returns>
    public static uint[] Render(int size, bool paused)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);

        var pixels = new uint[size * size];
        var (from, to) = paused ? (PausedFrom, PausedTo) : (ActiveFrom, ActiveTo);
        var glyph = paused ? PausedGlyph : ActiveGlyph;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var (body, mark) = Coverage(x, y, size, glyph);
                if (body == 0)
                {
                    continue;
                }

                var t = (x + y) / (2.0 * (size - 1)); // diagonal gradient
                var r = Mix(Lerp(from.R, to.R, t), 255, mark);
                var g = Mix(Lerp(from.G, to.G, t), 255, mark);
                var b = Mix(Lerp(from.B, to.B, t), 255, mark);
                pixels[(y * size) + x] = Pack(body * 255, r, g, b);
            }
        }

        return pixels;
    }

    /// <summary>Fraction of the pixel covered by the rounded square, and by the white glyph within it.</summary>
    private static (double Body, double Mark) Coverage(int x, int y, int size, (double L, double T, double R, double B)[] glyph)
    {
        int body = 0, mark = 0;
        for (var sy = 0; sy < Samples; sy++)
        {
            for (var sx = 0; sx < Samples; sx++)
            {
                var u = (x + ((sx + 0.5) / Samples)) / size;
                var v = (y + ((sy + 0.5) / Samples)) / size;
                if (!InRoundedSquare(u, v))
                {
                    continue;
                }

                body++;
                if (glyph.Any(r => InRoundedRect(u, v, r)))
                {
                    mark++;
                }
            }
        }

        return body == 0 ? (0, 0) : (body / (double)(Samples * Samples), mark / (double)body);
    }

    private static bool InRoundedSquare(double u, double v) => InRoundedRect(u, v, (0, 0, 1, 1), CornerRadius);

    private static bool InRoundedRect(double u, double v, (double L, double T, double R, double B) rect) =>
        InRoundedRect(u, v, rect, Math.Min(rect.R - rect.L, rect.B - rect.T) / 2);

    private static bool InRoundedRect(double u, double v, (double L, double T, double R, double B) rect, double radius)
    {
        if (u < rect.L || u > rect.R || v < rect.T || v > rect.B)
        {
            return false;
        }

        var dx = Math.Max(Math.Max(rect.L + radius - u, u - (rect.R - radius)), 0);
        var dy = Math.Max(Math.Max(rect.T + radius - v, v - (rect.B - radius)), 0);
        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private static double Mix(double color, double overlay, double amount) => Lerp(color, overlay, amount);

    private static uint Pack(double a, double r, double g, double b) =>
        ((uint)Math.Round(a) << 24) | ((uint)Math.Round(r) << 16) | ((uint)Math.Round(g) << 8) | (uint)Math.Round(b);
}
