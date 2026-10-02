using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TextFlow.Core.Expansion;
using TextFlow.Infrastructure.Input;
using TextFlow.Infrastructure.Windows;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextFlow.Infrastructure.Hooks;

public abstract record HookEvent;

/// <summary>A trigger was typed. If <see cref="TriggerMatch.Delimiter"/> is set, that key was swallowed and must be re-emitted if expansion is aborted.</summary>
public sealed record TriggerTyped(TriggerMatch Match, nint ForegroundWindow) : HookEvent;

public sealed record ForegroundChanged(nint Window) : HookEvent;

/// <summary>Content-free hook health counters.</summary>
public sealed record HookStats(long KeyEvents, double MaxCallbackMs);

/// <summary>
/// WH_KEYBOARD_LL + WH_MOUSE_LL + foreground WinEvent on a dedicated message-loop thread.
/// The callback must stay well under LowLevelHooksTimeout or Windows silently removes the hook,
/// so it only translates keys and feeds the <see cref="TriggerMatcher"/>; all real work is
/// consumed asynchronously from <see cref="Events"/>.
/// </summary>
public sealed unsafe class KeyboardHook : IDisposable
{
    private const uint DontChangeKeyboardState = 0x4; // ToUnicodeEx flag, Windows 10 1607+
    private const int TranslateBufferLength = 8;

    private static KeyboardHook? s_instance;

    private readonly TriggerMatcher _matcher;
    private readonly MessageLoopThread _thread;
    private readonly Channel<HookEvent> _events = Channel.CreateBounded<HookEvent>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

    private HHOOK _keyboardHook;
    private HHOOK _mouseHook;
    private UnhookWinEventSafeHandle? _foregroundHook;
    private char? _pendingDeadKey;
    private volatile bool _captureEnabled;
    private long _keyEvents;
    private long _maxCallbackTicks;

    public KeyboardHook(TriggerMatcher matcher)
    {
        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
        {
            throw new InvalidOperationException("Only one KeyboardHook may exist per process.");
        }

        _matcher = matcher;
        _thread = new MessageLoopThread("TextFlow.KeyboardHook");
        _thread.InvokeAsync(Install).GetAwaiter().GetResult();
    }

    public ChannelReader<HookEvent> Events => _events.Reader;

    public HookStats Stats => new(
        Interlocked.Read(ref _keyEvents),
        TimeSpan.FromTicks(Interlocked.Read(ref _maxCallbackTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency).TotalMilliseconds);

    /// <summary>
    /// Set by the policy layer after evaluating the foreground target. Fail closed: false until
    /// evaluation completes, so nothing typed into excluded apps or password fields is buffered.
    /// </summary>
    public bool CaptureEnabled
    {
        get => _captureEnabled;
        set
        {
            _captureEnabled = value;
            if (!value)
            {
                _thread.InvokeAsync(ResetState);
            }
        }
    }

    public Task ReplaceTriggersAsync(IEnumerable<TriggerDefinition> triggers) =>
        _thread.InvokeAsync(() => _matcher.ReplaceTriggers(triggers));

    private void Install()
    {
        var module = PInvoke.GetModuleHandle((PCWSTR)null);
        _keyboardHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &KeyboardProc, (HINSTANCE)module.Value, 0);
        _mouseHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &MouseProc, (HINSTANCE)module.Value, 0);
        _foregroundHook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND, default, &ForegroundProc, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT);

        if (_keyboardHook.IsNull || _mouseHook.IsNull || _foregroundHook is null || _foregroundHook.IsInvalid)
        {
            throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    private void ResetState()
    {
        _matcher.Reset();
        _pendingDeadKey = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
    {
        var self = s_instance;
        if (code >= 0 && self is not null && self.HandleKey((uint)wParam.Value, (KBDLLHOOKSTRUCT*)lParam.Value))
        {
            return new LRESULT(1); // swallow the delimiter of an AfterDelimiter trigger
        }

        return PInvoke.CallNextHookEx(default, code, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT MouseProc(int code, WPARAM wParam, LPARAM lParam)
    {
        var message = (uint)wParam.Value;
        if (code >= 0 && message is PInvoke.WM_LBUTTONDOWN or PInvoke.WM_RBUTTONDOWN or PInvoke.WM_MBUTTONDOWN)
        {
            s_instance?.ResetState(); // a click may move the caret: the buffer no longer reflects text before it
        }

        return PInvoke.CallNextHookEx(default, code, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ForegroundProc(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var self = s_instance;
        if (self is null)
        {
            return;
        }

        self.ResetState();
        self._events.Writer.TryWrite(new ForegroundChanged((nint)hwnd.Value));
    }

    private bool HandleKey(uint message, KBDLLHOOKSTRUCT* key)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (message is not (PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN)
                || key->dwExtraInfo == KeyboardInput.Signature
                || !_captureEnabled)
            {
                return false;
            }

            Interlocked.Increment(ref _keyEvents);
            return ProcessKeyDown((VIRTUAL_KEY)key->vkCode, key->scanCode);
        }
        finally
        {
            var elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed > _maxCallbackTicks)
            {
                Interlocked.Exchange(ref _maxCallbackTicks, elapsed);
            }
        }
    }

    private bool ProcessKeyDown(VIRTUAL_KEY vk, uint scanCode)
    {
        if (IsModifier(vk))
        {
            return false;
        }

        if (vk == VIRTUAL_KEY.VK_BACK)
        {
            _matcher.OnBackspace();
            _pendingDeadKey = null;
            return false;
        }

        var ctrl = IsDown(VIRTUAL_KEY.VK_CONTROL);
        var alt = IsDown(VIRTUAL_KEY.VK_MENU);
        var win = IsDown(VIRTUAL_KEY.VK_LWIN) || IsDown(VIRTUAL_KEY.VK_RWIN);
        var altGr = ctrl && alt;
        if (win || ((ctrl || alt) && !altGr) || IsNavigation(vk))
        {
            ResetState(); // shortcuts and navigation change what precedes the caret
            return false;
        }

        var text = Translate(vk, scanCode, altGr);
        var swallow = false;
        foreach (var c in text)
        {
            var match = _matcher.OnCharacter(c);
            if (match is not null)
            {
                _events.Writer.TryWrite(new TriggerTyped(match, (nint)PInvoke.GetForegroundWindow().Value));

                // Immediate triggers let their last key through: swallowing it could strand a pending
                // dead key in the target, and the backspace count already covers it.
                swallow |= match.Delimiter is not null;
            }
        }

        return swallow;
    }

    private string Translate(VIRTUAL_KEY vk, uint scanCode, bool altGr)
    {
        var state = stackalloc byte[256];
        if (IsDown(VIRTUAL_KEY.VK_SHIFT))
        {
            state[(int)VIRTUAL_KEY.VK_SHIFT] = 0x80;
        }

        if (altGr)
        {
            state[(int)VIRTUAL_KEY.VK_CONTROL] = 0x80;
            state[(int)VIRTUAL_KEY.VK_MENU] = 0x80;
        }

        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CAPITAL) & 0x1) != 0)
        {
            state[(int)VIRTUAL_KEY.VK_CAPITAL] = 0x01;
        }

        var foregroundThread = PInvoke.GetWindowThreadProcessId(PInvoke.GetForegroundWindow(), null);
        var layout = PInvoke.GetKeyboardLayout(foregroundThread);
        var buffer = stackalloc char[TranslateBufferLength];
        var count = PInvoke.ToUnicodeEx((uint)vk, scanCode, state, buffer, TranslateBufferLength, DontChangeKeyboardState, layout);

        if (count < 0)
        {
            var dead = buffer[0];
            var previous = _pendingDeadKey;
            _pendingDeadKey = dead;
            return previous is null ? string.Empty : previous.Value.ToString();
        }

        if (count == 0)
        {
            return string.Empty;
        }

        var produced = new string(buffer, 0, count);
        if (_pendingDeadKey is { } pending)
        {
            _pendingDeadKey = null;
            return DeadKeyComposer.Compose(pending, produced[0]) + produced[1..];
        }

        return produced;
    }

    private static bool IsDown(VIRTUAL_KEY vk) => (PInvoke.GetAsyncKeyState((int)vk) & 0x8000) != 0;

    private static bool IsModifier(VIRTUAL_KEY vk) => vk is
        VIRTUAL_KEY.VK_SHIFT or VIRTUAL_KEY.VK_LSHIFT or VIRTUAL_KEY.VK_RSHIFT or
        VIRTUAL_KEY.VK_CONTROL or VIRTUAL_KEY.VK_LCONTROL or VIRTUAL_KEY.VK_RCONTROL or
        VIRTUAL_KEY.VK_MENU or VIRTUAL_KEY.VK_LMENU or VIRTUAL_KEY.VK_RMENU or
        VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN or VIRTUAL_KEY.VK_CAPITAL;

    private static bool IsNavigation(VIRTUAL_KEY vk) => vk is
        VIRTUAL_KEY.VK_LEFT or VIRTUAL_KEY.VK_RIGHT or VIRTUAL_KEY.VK_UP or VIRTUAL_KEY.VK_DOWN or
        VIRTUAL_KEY.VK_HOME or VIRTUAL_KEY.VK_END or VIRTUAL_KEY.VK_PRIOR or VIRTUAL_KEY.VK_NEXT or
        VIRTUAL_KEY.VK_DELETE or VIRTUAL_KEY.VK_ESCAPE or VIRTUAL_KEY.VK_INSERT;

    public void Dispose()
    {
        _thread.InvokeAsync(() =>
        {
            PInvoke.UnhookWindowsHookEx(_keyboardHook);
            PInvoke.UnhookWindowsHookEx(_mouseHook);
            _foregroundHook?.Dispose();
        }).Wait(TimeSpan.FromSeconds(1));
        _thread.Dispose();
        _events.Writer.TryComplete();
        Interlocked.CompareExchange(ref s_instance, null, this);
    }
}
