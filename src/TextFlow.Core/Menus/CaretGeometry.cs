using TextFlow.Contracts.Targeting;

namespace TextFlow.Core.Menus;

/// <summary>
/// Turns UI Automation text-range rectangles into a caret rectangle for apps without a Win32 caret (Chrome; H4.4).
/// A collapsed caret range has no rectangles, so the caret is derived from the character before it (right edge) or,
/// failing that, after it (left edge). Only geometry: the text itself is never read.
/// </summary>
public static class CaretGeometry
{
    /// <summary>Taller than any line of text: a rectangle this high is a page or a block, not a caret position.</summary>
    private const int MaxLineHeight = 200;

    /// <summary>A character wider than this many line heights is a newline drawn to the margin.</summary>
    private const int MaxCharacterWidthInLines = 3;

    /// <summary>The end of a non-empty selection (where the caret sits after selecting forwards).</summary>
    public static PixelRect? FromSelection(IReadOnlyList<PixelRect> selection) =>
        selection.LastOrDefault(IsLine) is { } last && IsLine(last) ? RightEdge(last) : null;

    /// <param name="next">Rectangles of the character after the caret (empty at the end of the text).</param>
    /// <param name="previous">Rectangles of the character before the caret (empty at the start of the text).</param>
    public static PixelRect? FromNeighbours(IReadOnlyList<PixelRect> next, IReadOnlyList<PixelRect> previous)
    {
        var after = next.FirstOrDefault(IsLine);
        var before = previous.LastOrDefault(IsLine);
        var hasAfter = IsLine(after);
        var hasBefore = IsLine(before);

        // TextFlow asks right after the user typed a trigger, so the character before is that trigger's last one and
        // is on the caret's line. The one after is not reliable: at the end of a line it is the next line's start.
        if (hasBefore && IsCharacter(before))
        {
            return RightEdge(before);
        }

        if (hasAfter)
        {
            return LeftEdge(after);
        }

        return hasBefore ? RightEdge(before) : null;
    }

    private static bool IsLine(PixelRect rect) => rect.Height is > 0 and <= MaxLineHeight && rect != default;

    private static bool IsCharacter(PixelRect rect) => rect.Width > 0 && rect.Width <= rect.Height * MaxCharacterWidthInLines;

    private static PixelRect LeftEdge(PixelRect rect) => new(rect.Left, rect.Top, rect.Left + 1, rect.Bottom);

    private static PixelRect RightEdge(PixelRect rect) => new(rect.Right, rect.Top, rect.Right + 1, rect.Bottom);
}
