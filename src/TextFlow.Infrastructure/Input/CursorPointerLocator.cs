using TextFlow.Contracts.Targeting;
using TextFlow.Core.Engine;
using Windows.Win32;

namespace TextFlow.Infrastructure.Input;

/// <summary>Mouse pointer as a one-line "caret" (16 px tall) for targets that expose no caret.</summary>
public sealed class CursorPointerLocator : IPointerLocator
{
    private const int LineHeight = 16;

    public PixelRect CursorAnchor() =>
        PInvoke.GetCursorPos(out var p) ? new PixelRect(p.X, p.Y, p.X, p.Y + LineHeight) : default;
}
