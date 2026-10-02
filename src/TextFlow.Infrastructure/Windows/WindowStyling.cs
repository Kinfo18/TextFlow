using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace TextFlow.Infrastructure.Windows;

public static class WindowStyling
{
    /// <summary>Asks DWM for Windows 11 rounded corners (with native border and shadow). No-op on Windows 10.</summary>
    public static unsafe bool TryRoundCorners(nint window)
    {
        var preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        return PInvoke.DwmSetWindowAttribute(
            (HWND)window,
            DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
            &preference,
            (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE)).Succeeded;
    }
}
