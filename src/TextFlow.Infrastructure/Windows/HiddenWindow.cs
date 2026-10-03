using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextFlow.Infrastructure.Windows;

/// <summary>
/// Invisible window that receives messages (tray callbacks, WM_HOTKEY) on a <see cref="MessageLoopThread"/>.
/// Create, use and dispose it on that thread only.
/// </summary>
internal sealed unsafe class HiddenWindow : IDisposable
{
    private static readonly HWND MessageOnlyParent = new(-3); // HWND_MESSAGE
    private static readonly ConcurrentDictionary<nint, Func<uint, WPARAM, LPARAM, bool>> Handlers = new();
    private static int s_counter;

    private readonly string _className;
    private readonly HINSTANCE _module;

    /// <param name="purpose">Part of the window class name (diagnosis with Spy++).</param>
    /// <param name="messageOnly">
    /// Message-only windows are cheaper but miss broadcasts such as "TaskbarCreated"; the tray needs a hidden
    /// top-level window instead.
    /// </param>
    /// <param name="handler">Returns true when it handled the message.</param>
    public HiddenWindow(string purpose, bool messageOnly, Func<uint, WPARAM, LPARAM, bool> handler)
    {
        _className = $"TextFlow.{purpose}.{Environment.ProcessId}.{Interlocked.Increment(ref s_counter)}";
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

            Handle = PInvoke.CreateWindowEx(0, name, name, 0, 0, 0, 0, 0, messageOnly ? MessageOnlyParent : default, default, _module, null);
            if (Handle.IsNull)
            {
                var error = Marshal.GetLastPInvokeError();
                PInvoke.UnregisterClass(name, _module);
                throw new Win32Exception(error);
            }
        }

        Handlers[Handle] = handler;
    }

    public HWND Handle { get; private set; }

    /// <remarks>An exception must not escape a native callback (the process would fail fast): it is traced and the message gets default handling.</remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (Handlers.TryGetValue(hwnd, out var handler) && handler(message, wParam, lParam))
            {
                return (LRESULT)0;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"TextFlow hidden window handler failed: {ex.GetType().Name}");
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle.IsNull)
        {
            return;
        }

        Handlers.TryRemove(Handle, out _);
        PInvoke.DestroyWindow(Handle);
        Handle = default;
        fixed (char* name = _className)
        {
            PInvoke.UnregisterClass(name, _module);
        }
    }
}
