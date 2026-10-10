using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TextFlow.Core.Expansion;
using TextFlow.Core.Input;
using TextFlow.Core.Menus;
using TextFlow.Infrastructure.Input;
using TextFlow.Infrastructure.Windows;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextFlow.Infrastructure.Hooks;

/// <summary>
/// WH_KEYBOARD_LL + WH_MOUSE_LL + foreground WinEvent on a dedicated message-loop thread.
/// The callback must stay well under LowLevelHooksTimeout or Windows silently removes the hook,
/// so it only translates keys and feeds the <see cref="TriggerMatcher"/>; all real work is
/// consumed asynchronously from <see cref="Events"/>.
/// </summary>
public sealed unsafe class KeyboardHook : IInputHook, IReinstallableHook, IDisposable
{
    private const uint DontChangeKeyboardState = 0x4; // ToUnicodeEx flag, Windows 10 1607+
    private const int TranslateBufferLength = 8;

    private static KeyboardHook? s_instance;
    private static readonly PointerReleased PointerReleasedEvent = new(); // no allocation per click

    private readonly TriggerMatcher _matcher;
    private readonly MessageLoopThread _thread;
    private readonly Channel<HookEvent> _events = Channel.CreateBounded<HookEvent>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

    private HHOOK _keyboardHook;
    private HHOOK _mouseHook;
    private UnhookWinEventSafeHandle? _foregroundHook;
    private UnhookWinEventSafeHandle? _focusHook;
    private int _focusVersion;
    private char? _pendingDeadKey;
    private volatile bool _captureEnabled;
    private volatile bool _menuMode;
    private bool _menuLeftByTyping; // hook thread only: the matcher already holds what follows the menu
    private readonly KnownFields _knownFields = new(); // hook thread only
    /// <summary>At most one <see cref="TypingActivity"/> per interval: enough to keep audio awake, cheap for the hook.</summary>
    private static readonly long ActivityInterval = Stopwatch.Frequency * 4;

    private long _keyEvents;
    private int _lastCallbackTick;
    private long _lastActivityAt;
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
                _thread.InvokeAsync(ForgetText);
            }
        }
    }

    public int FocusVersion => Volatile.Read(ref _focusVersion);

    public void ForgetKnownFields() => _knownFields.Clear();

    public bool TryEnableCapture(int focusVersion)
    {
        _captureEnabled = true;
        if (Volatile.Read(ref _focusVersion) == focusVersion)
        {
            _knownFields.Allowed(focusVersion);
            return true;
        }

        // Focus moved while the engine evaluated the old control: the hook thread already turned capture off for
        // the new one, and the engine will evaluate it next. Undo our stale "on".
        _captureEnabled = false;
        return false;
    }

    /// <summary>
    /// While true, navigation keys (arrows, Enter, Esc, 1-9) are swallowed and reported as
    /// <see cref="MenuKeyPressed"/>, so the target keeps focus and caret while a non-activating menu is shown.
    /// Any other key or mouse press is reported as <see cref="MenuInterrupted"/> and passes through.
    /// </summary>
    public bool MenuMode
    {
        get => _menuMode;
        set
        {
            _menuMode = value;
            if (!value)
            {
                // Opening keeps the pending trigger so "cp" + '1' still reaches "cp1". A menu left by typing on
                // keeps the matcher as the keys left it; a choice or a shortcut starts over.
                _thread.InvokeAsync(() =>
                {
                    if (!_menuLeftByTyping)
                    {
                        ResetState();
                    }

                    _menuLeftByTyping = false;
                });
            }
        }
    }

    /// <summary>Fires the pending trigger (as <see cref="TriggerTyped"/>) if nothing was typed since <paramref name="version"/>.</summary>
    public Task FlushPendingAsync(int version) => _thread.InvokeAsync(() =>
    {
        if (_matcher.FlushPending(version) is { } match)
        {
            _events.Writer.TryWrite(new TriggerTyped(match, (nint)PInvoke.GetForegroundWindow().Value));
        }
    });

    public Task ReplaceTriggersAsync(IEnumerable<TriggerDefinition> triggers) =>
        _thread.InvokeAsync(() => _matcher.ReplaceTriggers(triggers));

    /// <summary>Environment.TickCount (same clock as GetLastInputInfo) of the last keyboard or mouse callback.</summary>
    public uint LastCallbackTick => unchecked((uint)Volatile.Read(ref _lastCallbackTick));

    /// <summary>Hooks the low-level keyboard and mouse again (the WinEvent hook is never removed by Windows).</summary>
    public Task ReinstallAsync() => _thread.InvokeAsync(() =>
    {
        PInvoke.UnhookWindowsHookEx(_keyboardHook); // fails harmlessly if Windows already removed it
        PInvoke.UnhookWindowsHookEx(_mouseHook);
        InstallLowLevel();
        ResetState();
    });

    /// <summary>Tests only: unhooks without telling anyone, exactly what Windows does after a callback timeout.</summary>
    internal Task SimulateSilentRemovalAsync() => _thread.InvokeAsync(() =>
    {
        PInvoke.UnhookWindowsHookEx(_keyboardHook);
        PInvoke.UnhookWindowsHookEx(_mouseHook);
    });

    private void InstallLowLevel()
    {
        var module = PInvoke.GetModuleHandle((PCWSTR)null);
        _keyboardHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &KeyboardProc, (HINSTANCE)module.Value, 0);
        _mouseHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &MouseProc, (HINSTANCE)module.Value, 0);
        Volatile.Write(ref _lastCallbackTick, Environment.TickCount); // a fresh hook has not missed anything yet
        if (_keyboardHook.IsNull || _mouseHook.IsNull)
        {
            throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    private void Install()
    {
        InstallLowLevel();
        _foregroundHook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND, default, &ForegroundProc, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT);

        _focusHook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_OBJECT_FOCUS, PInvoke.EVENT_OBJECT_FOCUS, default, &FocusProc, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT);

        if (_foregroundHook is null || _foregroundHook.IsInvalid || _focusHook is null || _focusHook.IsInvalid)
        {
            throw new InvalidOperationException($"SetWinEventHook failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    private void ResetState()
    {
        _matcher.Reset();
        _pendingDeadKey = null;
    }

    /// <summary>Like <see cref="ResetState"/>, but the caret may still be inside a word (focus noise, capture off).</summary>
    private void ForgetText()
    {
        _matcher.Forget();
        _pendingDeadKey = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
    {
        var self = s_instance;
        if (self is not null)
        {
            Volatile.Write(ref self._lastCallbackTick, Environment.TickCount); // watchdog: the hook is alive
        }

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
        if (s_instance is { } alive)
        {
            Volatile.Write(ref alive._lastCallbackTick, Environment.TickCount);
        }

        if (code >= 0 && message is PInvoke.WM_LBUTTONDOWN or PInvoke.WM_RBUTTONDOWN or PInvoke.WM_MBUTTONDOWN && s_instance is { } self)
        {
            self.ResetState(); // a click may move the caret: the buffer no longer reflects text before it
            if (self._menuMode)
            {
                var point = ((MSLLHOOKSTRUCT*)lParam.Value)->pt;
                self._events.Writer.TryWrite(new MenuInterrupted(point.X, point.Y));
            }
        }
        else if (code >= 0 && message == PInvoke.WM_LBUTTONUP && s_instance is { } released)
        {
            released._events.Writer.TryWrite(PointerReleasedEvent);
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

        Interlocked.Increment(ref self._focusVersion);
        self._captureEnabled = false; // fail closed until the engine has evaluated the new foreground
        self._knownFields.ForegroundChanged();
        self.ResetState();
        self._events.Writer.TryWrite(new ForegroundChanged((nint)hwnd.Value));
    }

    /// <summary>
    /// Focus moved inside a window (R4: user name → password field of a web login). Fail closed until the engine
    /// re-evaluated the focused control. Ignored while a group menu is open: it owns the keys and closes itself.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void FocusProc(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var self = s_instance;
        if (self is null || self._menuMode)
        {
            return;
        }

        var field = new FieldId((nint)hwnd.Value, idObject, idChild);
        if (self._captureEnabled && self._knownFields.IsKnown(field))
        {
            // Back on a field already allowed in this window (Notepad bounces the focus after each paste): keep the
            // typed text and keep capturing; the engine re-checks it and turns capture off if it is no longer allowed.
            self._events.Writer.TryWrite(new FocusChanged((nint)hwnd.Value, KnownField: true));
            return;
        }

        var version = Interlocked.Increment(ref self._focusVersion);
        self._captureEnabled = false;
        self._knownFields.Evaluating(field, version);
        self.ForgetText(); // a click already reset; browsers also fire focus mid-word, which must not look like a word start
        self._events.Writer.TryWrite(new FocusChanged((nint)hwnd.Value));
    }

    private bool HandleKey(uint message, KBDLLHOOKSTRUCT* key)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (message is not (PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN) || key->dwExtraInfo == KeyboardInput.Signature)
            {
                return false;
            }

            if (!_captureEnabled)
            {
                NoteUnseenKey((VIRTUAL_KEY)key->vkCode);
                return false;
            }

            Interlocked.Increment(ref _keyEvents);
            ReportActivity(started);
            return _menuMode
                ? ProcessMenuKey((VIRTUAL_KEY)key->vkCode, key->scanCode)
                : ProcessKeyDown((VIRTUAL_KEY)key->vkCode, key->scanCode);
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

    private void ReportActivity(long now)
    {
        if (now - _lastActivityAt >= ActivityInterval)
        {
            _lastActivityAt = now;
            _events.Writer.TryWrite(new TypingActivity());
        }
    }

    /// <summary>
    /// Capture is off (focus being evaluated, paused, excluded app): the key is never translated or stored, only
    /// classified, so a trigger typed right after it is not taken for a word start.
    /// </summary>
    private void NoteUnseenKey(VIRTUAL_KEY vk)
    {
        if (IsModifier(vk) || vk == VIRTUAL_KEY.VK_BACK)
        {
            return;
        }

        if (IsShortcutDown() || IsNavigation(vk) || vk is VIRTUAL_KEY.VK_SPACE or VIRTUAL_KEY.VK_RETURN or VIRTUAL_KEY.VK_TAB)
        {
            ResetState();
            return;
        }

        _matcher.OnUnseenText();
    }

    private bool ProcessMenuKey(VIRTUAL_KEY vk, uint scanCode)
    {
        if (IsModifier(vk))
        {
            return false;
        }

        var shortcut = IsDown(VIRTUAL_KEY.VK_CONTROL) || IsDown(VIRTUAL_KEY.VK_MENU) || IsDown(VIRTUAL_KEY.VK_LWIN) || IsDown(VIRTUAL_KEY.VK_RWIN);
        if (!shortcut && _matcher.HasPending && ContinuePendingFromMenu(vk, scanCode))
        {
            return false; // the key belongs to a longer trigger ("cp" menu open, user types '1' for "cp1"): let it through
        }

        if (!shortcut && ToMenuInput(vk) is { } input)
        {
            if (input.Kind == MenuInputKind.Escape)
            {
                _matcher.CancelPending(); // "g" stays typed: what follows is glued to it
                _menuLeftByTyping = true;
            }

            _events.Writer.TryWrite(new MenuKeyPressed(input));
            return true;
        }

        _events.Writer.TryWrite(new MenuInterrupted());
        if (shortcut)
        {
            return false;
        }

        // The user kept typing ("g" menu, then "e"): the key is ordinary text and the matcher must see it, or the
        // next trigger would look like a word start ("dir" menu, then "ección" fired "cc").
        _menuLeftByTyping = true;
        _matcher.CancelPending();
        return ProcessKeyDown(vk, scanCode);
    }

    private bool ContinuePendingFromMenu(VIRTUAL_KEY vk, uint scanCode)
    {
        if (IsNavigation(vk) || vk is VIRTUAL_KEY.VK_RETURN or VIRTUAL_KEY.VK_BACK)
        {
            return false;
        }

        var text = Translate(vk, scanCode, altGr: false);
        if (text.Length != 1 || !_matcher.ContinuesPending(text[0]))
        {
            return false;
        }

        var version = _matcher.PendingVersion;
        if (_matcher.OnCharacter(text[0]) is { } match)
        {
            _events.Writer.TryWrite(new TriggerTyped(match, (nint)PInvoke.GetForegroundWindow().Value));
            return true; // the engine closes the menu for the longer trigger
        }

        // Still on the way to a longer trigger ("g" menu, typing "gracias"): hide the menu, keep the pending trigger.
        _menuLeftByTyping = true;
        _events.Writer.TryWrite(new MenuInterrupted());
        if (_matcher.PendingMatch is { } pending && _matcher.PendingVersion != version)
        {
            _events.Writer.TryWrite(new TriggerPending(pending, _matcher.PendingVersion, (nint)PInvoke.GetForegroundWindow().Value));
        }

        return true;
    }

    private static MenuInput? ToMenuInput(VIRTUAL_KEY vk) => vk switch
    {
        VIRTUAL_KEY.VK_UP => MenuInput.Up,
        VIRTUAL_KEY.VK_DOWN => MenuInput.Down,
        VIRTUAL_KEY.VK_LEFT => MenuInput.Left,
        VIRTUAL_KEY.VK_RIGHT => MenuInput.Right,
        VIRTUAL_KEY.VK_RETURN => MenuInput.Enter,
        VIRTUAL_KEY.VK_ESCAPE => MenuInput.Escape,
        >= VIRTUAL_KEY.VK_1 and <= VIRTUAL_KEY.VK_9 => MenuInput.Number(vk - VIRTUAL_KEY.VK_0),
        >= VIRTUAL_KEY.VK_NUMPAD1 and <= VIRTUAL_KEY.VK_NUMPAD9 => MenuInput.Number(vk - VIRTUAL_KEY.VK_NUMPAD0),
        _ => null,
    };

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

        var altGr = IsDown(VIRTUAL_KEY.VK_CONTROL) && IsDown(VIRTUAL_KEY.VK_MENU);
        if (IsShortcutDown() || IsNavigation(vk))
        {
            ResetState(); // shortcuts and navigation change what precedes the caret
            return false;
        }

        var text = Translate(vk, scanCode, altGr);
        var swallow = false;
        var pendingBefore = _matcher.PendingVersion;
        foreach (var c in text)
        {
            var match = _matcher.OnCharacter(c);
            if (match is not null)
            {
                _events.Writer.TryWrite(new TriggerTyped(match, (nint)PInvoke.GetForegroundWindow().Value));

                // Immediate triggers let their last key through: swallowing it could strand a pending
                // dead key in the target, and the backspace count already covers it. A Delimiter is set for
                // AfterDelimiter triggers and for a pending trigger broken by the next key: that key is swallowed.
                swallow |= match.Delimiter is not null;
            }
        }

        if (_matcher.PendingMatch is { } pending && _matcher.PendingVersion != pendingBefore)
        {
            _events.Writer.TryWrite(new TriggerPending(pending, _matcher.PendingVersion, (nint)PInvoke.GetForegroundWindow().Value));
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

    /// <summary>Win, Ctrl or Alt held, except AltGr (Ctrl+Alt), which types characters such as '@'.</summary>
    private static bool IsShortcutDown()
    {
        var ctrl = IsDown(VIRTUAL_KEY.VK_CONTROL);
        var alt = IsDown(VIRTUAL_KEY.VK_MENU);
        return IsDown(VIRTUAL_KEY.VK_LWIN) || IsDown(VIRTUAL_KEY.VK_RWIN) || (ctrl ^ alt);
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
            _focusHook?.Dispose();
        }).Wait(TimeSpan.FromSeconds(1));
        _thread.Dispose();
        _events.Writer.TryComplete();
        Interlocked.CompareExchange(ref s_instance, null, this);
    }
}
