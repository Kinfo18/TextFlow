using System.Runtime.InteropServices;

namespace TextFlow.WinUiSpike;

/// <summary>Win32 calls the H0.1 spike needs beyond what WinUI/AppWindow exposes.</summary>
internal static partial class Native
{
    public const int GwlExStyle = -20;
    public const long WsExNoActivate = 0x08000000;
    public const long WsExToolWindow = 0x00000080;
    public const long WsExTopMost = 0x00000008;
    public const uint WmMouseActivate = 0x0021;
    public const nint MaNoActivate = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public Rect CaretRect;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point point);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool SetWindowSubclass(
        nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    public static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>Foreground window and the focused control of its GUI thread: what must not change.</summary>
    public static (nint Foreground, nint Focus) FocusSnapshot()
    {
        var foreground = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? (foreground, info.Focus) : (foreground, 0);
    }

    public static (int X, int Y, int Height)? CaretOrCursor()
    {
        var foreground = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(thread, ref info) && info.Caret != 0)
        {
            var p = new Point { X = info.CaretRect.Left, Y = info.CaretRect.Bottom };
            if (ClientToScreen(info.Caret, ref p))
            {
                return (p.X, p.Y, info.CaretRect.Bottom - info.CaretRect.Top);
            }
        }

        return GetCursorPos(out var cursor) ? (cursor.X, cursor.Y + 16, 16) : null;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, byte* info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    /// <summary>Copies a screen rectangle (what the user actually sees) into a 32-bit BMP file.</summary>
    public static unsafe void CaptureScreen(Rect r, string path)
    {
        var (w, h) = (r.Right - r.Left, r.Bottom - r.Top);
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
        SelectObject(mem, old);

        var header = new byte[40];
        BitConverter.GetBytes(40).CopyTo(header, 0);
        BitConverter.GetBytes(w).CopyTo(header, 4);
        BitConverter.GetBytes(-h).CopyTo(header, 8); // top-down
        BitConverter.GetBytes((short)1).CopyTo(header, 12);
        BitConverter.GetBytes((short)32).CopyTo(header, 14);
        var pixels = new byte[w * h * 4];
        fixed (byte* info = header)
        fixed (byte* bits = pixels)
        {
            if (GetDIBits(mem, bmp, 0, (uint)h, bits, info, 0) == 0)
            {
                throw new InvalidOperationException("GetDIBits failed.");
            }
        }

        DeleteObject(bmp);
        DeleteDC(mem);
        _ = ReleaseDC(0, screen);

        using var file = File.Create(path);
        var fileHeader = new byte[14];
        fileHeader[0] = (byte)'B';
        fileHeader[1] = (byte)'M';
        BitConverter.GetBytes(14 + 40 + pixels.Length).CopyTo(fileHeader, 2);
        BitConverter.GetBytes(14 + 40).CopyTo(fileHeader, 10);
        file.Write(fileHeader);
        BitConverter.GetBytes(0).CopyTo(header, 20);
        file.Write(header);
        file.Write(pixels);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    public static double DpiScale(nint hwnd) => GetDpiForWindow(hwnd) is var dpi and > 0 ? dpi / 96.0 : 1.0;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, ref Point point);
}
