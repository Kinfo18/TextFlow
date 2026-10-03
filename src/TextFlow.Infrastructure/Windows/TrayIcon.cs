using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TextFlow.Core.Feedback;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextFlow.Infrastructure.Windows;

public enum TrayCommand
{
    Open,
    TogglePause,
    Exit,
}

/// <summary>
/// Notification-area icon with the TextFlow menu (Abrir · Pausar/Reanudar · Salir). Runs on its own
/// message-loop thread with a hidden top-level window (a message-only one would miss "TaskbarCreated",
/// so the icon would vanish when Explorer restarts). One instance per process.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 2; // WM_APP + 2
    private const uint IconId = 1;
    private const uint OpenId = 1;
    private const uint PauseId = 2;
    private const uint ExitId = 3;

    private static TrayIcon? s_instance;

    private readonly MessageLoopThread _thread;
    private readonly string _className = $"TextFlow.Tray.{Environment.ProcessId}";
    private HWND _window;
    private HICON _activeIcon;
    private HICON _pausedIcon;
    private uint _taskbarCreated;
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan[] AddRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private HINSTANCE _module;
    private volatile bool _paused;
    private volatile bool _added;
    private volatile bool _disposed;

    public TrayIcon()
    {
        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
        {
            throw new InvalidOperationException("Only one tray icon per process.");
        }

        _thread = new MessageLoopThread("TextFlow.Tray");
        try
        {
            _thread.InvokeAsync(Create).GetAwaiter().GetResult();
        }
        catch
        {
            _thread.InvokeAsync(Destroy).Wait(DisposeTimeout);
            _thread.Dispose();
            Interlocked.CompareExchange(ref s_instance, null, this);
            throw;
        }

        if (!_added)
        {
            _ = RetryAddAsync(); // at logon Explorer's tray may not be ready yet
        }
    }

    /// <summary>Raised on a thread-pool thread if the icon could not be added even after retries: show the window instead.</summary>
    public event Action? Unavailable;

    /// <summary>Raised on a thread-pool thread when the user picks a command.</summary>
    public event Action<TrayCommand>? CommandInvoked;

    /// <summary>Gray icon, "en pausa" tooltip and "Reanudar" menu entry while paused.</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        _ = _thread.InvokeAsync(() => Notify(NotifyMessage.Modify));
    }

    private unsafe void Create()
    {
        var size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForSystem());
        _activeIcon = CreateIcon(size, paused: false);
        _pausedIcon = CreateIcon(size, paused: true);

        _module = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        fixed (char* name = _className)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = _module,
                lpszClassName = name,
            };
            if (PInvoke.RegisterClassEx(windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            _window = PInvoke.CreateWindowEx(0, name, name, 0, 0, 0, 0, 0, default, default, _module, null);
        }

        if (_window.IsNull)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        _added = Notify(NotifyMessage.Add);
    }

    private async Task RetryAddAsync()
    {
        foreach (var delay in AddRetryDelays)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            if (_disposed || _added)
            {
                return;
            }

            _added = await _thread.InvokeAsync(() => Notify(NotifyMessage.Add)).ConfigureAwait(false);
        }

        if (!_added && !_disposed)
        {
            Unavailable?.Invoke();
        }
    }

    private unsafe bool Notify(NotifyMessage message)
    {
        if (_window.IsNull)
        {
            return false;
        }

        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = _window,
            Id = IconId,
            Flags = NotifyIconData.FlagMessage | NotifyIconData.FlagIcon | NotifyIconData.FlagTip,
            CallbackMessage = CallbackMessage,
            Icon = _paused ? _pausedIcon : _activeIcon,
        };
        var tip = _paused ? "TextFlow · en pausa" : "TextFlow · activo";
        tip.AsSpan().CopyTo(new Span<char>(data.Tip, NotifyIconData.TipLength - 1));
        return ShellNotifyIcon(message, &data);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        var self = s_instance;
        if (self is not null && self._window == hwnd && self.Handle(message, lParam))
        {
            return (LRESULT)0;
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private bool Handle(uint message, LPARAM lParam)
    {
        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            _added = Notify(NotifyMessage.Add); // Explorer restarted
            return true;
        }

        if (message != CallbackMessage)
        {
            return false;
        }

        switch ((uint)lParam.Value)
        {
            case PInvoke.WM_LBUTTONUP:
                Raise(TrayCommand.Open);
                break;
            case PInvoke.WM_RBUTTONUP:
                ShowMenu();
                break;
        }

        return true;
    }

    private unsafe void ShowMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        try
        {
            Append(menu, OpenId, "Abrir TextFlow");
            Append(menu, PauseId, _paused ? "Reanudar expansiones" : "Pausar expansiones");
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
            Append(menu, ExitId, "Salir");
            PInvoke.SetMenuDefaultItem(menu, OpenId, 0);

            PInvoke.GetCursorPos(out var point);
            PInvoke.SetForegroundWindow(_window); // otherwise the menu does not close when clicking elsewhere
            var chosen = PInvoke.TrackPopupMenu(
                menu,
                TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY,
                point.X,
                point.Y,
                0,
                _window,
                null);
            PInvoke.PostMessage(_window, PInvoke.WM_NULL, default, default);

            switch ((uint)chosen.Value)
            {
                case OpenId:
                    Raise(TrayCommand.Open);
                    break;
                case PauseId:
                    Raise(TrayCommand.TogglePause);
                    break;
                case ExitId:
                    Raise(TrayCommand.Exit);
                    break;
            }
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    private unsafe static void Append(HMENU menu, uint id, string text)
    {
        fixed (char* label = text)
        {
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, id, label);
        }
    }

    /// <summary>Off the tray thread: subscribers may block (open a window, stop the engine).</summary>
    private void Raise(TrayCommand command) => ThreadPool.QueueUserWorkItem(_ => CommandInvoked?.Invoke(command));

    private unsafe static HICON CreateIcon(int size, bool paused)
    {
        var pixels = TrayIconArt.Render(size, paused);
        var header = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = size,
                biHeight = -size, // top-down, like the pixel array
                biPlanes = 1,
                biBitCount = 32,
            },
        };

        void* bits;
        var color = PInvoke.CreateDIBSection(default, &header, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
        var mask = PInvoke.CreateBitmap(size, size, 1, 1, null); // all zero: the alpha channel decides
        try
        {
            if (color.IsNull || mask.IsNull)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            pixels.CopyTo(new Span<uint>(bits, pixels.Length));
            var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            var icon = PInvoke.CreateIconIndirect(&info);
            return icon.IsNull ? throw new Win32Exception(Marshal.GetLastPInvokeError()) : icon;
        }
        finally
        {
            PInvoke.DeleteObject(color);
            PInvoke.DeleteObject(mask);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _thread.InvokeAsync(Destroy).Wait(DisposeTimeout);
        _thread.Dispose();
        Interlocked.CompareExchange(ref s_instance, null, this);
    }

    private unsafe void UnregisterWindowClass()
    {
        fixed (char* name = _className)
        {
            PInvoke.UnregisterClass(name, _module);
        }
    }

    /// <summary>Releases whatever <see cref="Create"/> got to (also after a partial failure). Tray thread only.</summary>
    private void Destroy()
    {
        if (!_window.IsNull)
        {
            Notify(NotifyMessage.Delete);
            PInvoke.DestroyWindow(_window);
            _window = default;
        }

        if (!_module.IsNull)
        {
            UnregisterWindowClass();
        }

        if (!_activeIcon.IsNull)
        {
            PInvoke.DestroyIcon(_activeIcon);
        }

        if (!_pausedIcon.IsNull)
        {
            PInvoke.DestroyIcon(_pausedIcon);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private unsafe static partial bool ShellNotifyIcon(NotifyMessage message, NotifyIconData* data);

    private enum NotifyMessage : uint
    {
        Add = 0,
        Modify = 1,
        Delete = 2,
    }

    /// <summary>
    /// NOTIFYICONDATAW, x64 layout (CsWin32 only generates it per architecture and Infrastructure is AnyCPU;
    /// the app ships x64 only, ADR-0008).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct NotifyIconData
    {
        public const uint FlagMessage = 0x1;
        public const uint FlagIcon = 0x2;
        public const uint FlagTip = 0x4;
        public const int TipLength = 128;

        public uint Size;
        public HWND Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public HICON Icon;
        public fixed char Tip[TipLength];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public HICON BalloonIcon;
    }
}
