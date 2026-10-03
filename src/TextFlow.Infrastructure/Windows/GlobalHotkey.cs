using System.Diagnostics;
using TextFlow.Core.Input;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace TextFlow.Infrastructure.Windows;

/// <summary>
/// System-wide shortcut through RegisterHotKey (works in every app, even while capture is paused).
/// Registration fails when another app already owns the gesture: check <see cref="IsRegistered"/>.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 1;
    private const uint NoRepeat = 0x4000; // MOD_NOREPEAT: holding the keys fires once

    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    private readonly MessageLoopThread _thread;
    private HiddenWindow? _window;
    private volatile bool _disposed;

    public GlobalHotkey(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        Gesture = gesture;
        _thread = new MessageLoopThread("TextFlow.Hotkey");
        try
        {
            IsRegistered = _thread.InvokeAsync(Register).GetAwaiter().GetResult();
        }
        catch
        {
            _thread.Dispose();
            throw;
        }
    }

    /// <summary>Raised on a thread-pool thread each time the shortcut is pressed.</summary>
    public event Action? Pressed;

    public HotkeyGesture Gesture { get; }

    public bool IsRegistered { get; }

    private bool Register()
    {
        _window = new HiddenWindow("Hotkey", messageOnly: true, (message, wParam, _) =>
        {
            if (message != PInvoke.WM_HOTKEY || (int)wParam.Value != HotkeyId)
            {
                return false;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!_disposed)
                {
                    Pressed?.Invoke();
                }
            });
            return true;
        });

        return PInvoke.RegisterHotKey(_window.Handle, HotkeyId, (HOT_KEY_MODIFIERS)((uint)Gesture.Modifiers | NoRepeat), Gesture.VirtualKey);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Pressed = null;
        try
        {
            _thread.InvokeAsync(() =>
            {
                if (_window is not null)
                {
                    PInvoke.UnregisterHotKey(_window.Handle, HotkeyId);
                    _window.Dispose();
                }
            }).Wait(DisposeTimeout);
        }
        catch (AggregateException ex)
        {
            Trace.TraceError($"TextFlow hotkey cleanup failed: {ex.InnerException?.GetType().Name}");
        }
        finally
        {
            _thread.Dispose(); // ending the thread also releases the hotkey and the window
        }
    }
}
