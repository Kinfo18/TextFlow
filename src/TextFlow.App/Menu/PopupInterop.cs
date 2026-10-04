using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TextFlow.App.Menu;

/// <summary>Win32 calls the non-activating popup needs beyond AppWindow (ADR-0008). x64 only.</summary>
internal static unsafe partial class PopupInterop
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const uint WmMouseActivate = 0x0021;
    private const nint MaNoActivate = 3;

    /// <summary>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW plus a subclass answering WM_MOUSEACTIVATE with MA_NOACTIVATE.</summary>
    public static void MakeNonActivating(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, (nint)((long)style | WsExNoActivate | WsExToolWindow));
        SetWindowSubclass(hwnd, &NoActivateProc, 1, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint NoActivateProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data) =>
        message == WmMouseActivate ? MaNoActivate : DefSubclassProc(hwnd, message, wParam, lParam);

    /// <summary>
    /// Puts the popup above every non-topmost window without activating it. The presenter's IsAlwaysOnTop never set
    /// WS_EX_TOPMOST here, so a maximized Word could cover the menu (2026-10-04): enforce it on every show.
    /// </summary>
    public static void BringToTopmost(nint hwnd) =>
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder);

    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Window DPI as a scale factor (1.25 at 125 %); 1 when Windows cannot tell.</summary>
    public static double DpiScale(nint hwnd) => GetDpiForWindow(hwnd) is var dpi and > 0 ? dpi / 96.0 : 1.0;

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(
        nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
