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
