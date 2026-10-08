using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Menus;

namespace TextFlow.Infrastructure.Targeting;

/// <summary>
/// Caret position from UI Automation text ranges, for apps without a Win32 caret (Chrome, some Firefox fields; H4.4).
/// Reads bounding rectangles only, never <c>GetText</c>. Caller time-boxes it: providers can be slow.
/// </summary>
internal static class UiaCaretReader
{
    public static PixelRect? Read(AutomationElement element)
    {
        var range = CaretRange(element);
        if (range is null)
        {
            return null;
        }

        var selected = Rects(range);
        if (selected.Length > 0)
        {
            return CaretGeometry.FromSelection(selected);
        }

        return CaretGeometry.FromNeighbours(next: Neighbour(range, forward: true), previous: Neighbour(range, forward: false));
    }

    /// <summary>TextPattern2.GetCaretRange where supported (Chrome, Edge), else the first selection range.</summary>
    internal static ITextRange? CaretRange(AutomationElement element)
    {
        var patterns = element.Patterns;
        if (patterns.Text2.TryGetPattern(out var text2))
        {
            var caret = text2.GetCaretRange(out _);
            if (caret is not null)
            {
                return caret;
            }
        }

        return patterns.Text.TryGetPattern(out var text) && text.GetSelection() is [var first, ..] ? first : null;
    }

    /// <summary>The character right after (or before) a collapsed range, as rectangles.</summary>
    private static PixelRect[] Neighbour(ITextRange caret, bool forward)
    {
        var range = caret.Clone();
        var moved = forward
            ? range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1)
            : range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
        return moved == 0 ? [] : Rects(range);
    }

    private static PixelRect[] Rects(ITextRange range) =>
        [.. range.GetBoundingRectangles().Select(r => new PixelRect(r.Left, r.Top, r.Right, r.Bottom))];
}
