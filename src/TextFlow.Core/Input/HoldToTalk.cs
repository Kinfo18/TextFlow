namespace TextFlow.Core.Input;

/// <summary>One key edge as the low-level hook sees it.</summary>
/// <param name="VirtualKey">Left/right-specific VK (the LL hook reports VK_RCONTROL, not VK_CONTROL).</param>
/// <param name="ScanCode">AltGr's fake Left Ctrl carries 0x21D; kept for diagnostics.</param>
public sealed record KeyStroke(uint VirtualKey, uint ScanCode, bool IsDown, bool IsInjected, long TimestampMs);

public enum HoldResult
{
    None,
    Started,
    Stopped,
    Cancelled,

    /// <summary>Released too soon to be speech (a tap of the key): drop the recording, insert nothing.</summary>
    Discarded,
}

/// <summary>
/// Hold-to-talk detector for the dictation key (ADR-0003, V0.2 H0.4). Dictation starts when the key stays down for
/// <c>holdMs</c> with nothing else pressed, and stops when it is released. Any other key before the threshold makes it
/// a shortcut (Right Ctrl + C copies, it never dictates); any other key while talking cancels, because the user wanted a
/// shortcut and nothing must be inserted. Releasing before <c>minHoldMs</c> (counted from the press) is a tap, not a
/// phrase: the user's taps measured ~415 ms in the H0.4 probe, so recording starts early but such holds are discarded.
/// The key itself is never swallowed, so shortcuts keep working: the caller only
/// reports edges and calls <see cref="Tick"/> from a timer, since a held modifier does not always auto-repeat.
/// Injected keys (TextFlow's own Ctrl+V) are ignored. Not thread-safe: feed it from the hook thread only.
/// </summary>
public sealed class HoldToTalk(uint triggerKey, int holdMs = HoldToTalk.DefaultHoldMs, int minHoldMs = HoldToTalk.DefaultMinHoldMs)
{
    public const int DefaultHoldMs = 150;
    public const int DefaultMinHoldMs = 600;

    private enum State
    {
        Idle,
        Pressed,
        Shortcut,
        Talking,
    }

    private State _state = State.Idle;
    private long _pressedAt;

    public bool IsTalking => _state == State.Talking;

    public HoldResult Feed(KeyStroke key)
    {
        if (key.IsInjected)
        {
            return HoldResult.None;
        }

        if (key.VirtualKey == triggerKey)
        {
            return key.IsDown ? TriggerDown(key.TimestampMs) : TriggerUp(key.TimestampMs);
        }

        return key.IsDown ? OtherKeyDown() : HoldResult.None;
    }

    /// <summary>Called by the caller's timer while the key is down: starts dictation once the threshold has passed.</summary>
    public HoldResult Tick(long nowMs)
    {
        if (_state == State.Pressed && nowMs - _pressedAt >= holdMs)
        {
            _state = State.Talking;
            return HoldResult.Started;
        }

        return HoldResult.None;
    }

    /// <summary>A mouse press before the threshold (Ctrl+click selects): the hold becomes a shortcut.</summary>
    public void Interrupt()
    {
        if (_state == State.Pressed)
        {
            _state = State.Shortcut;
        }
    }

    private HoldResult TriggerDown(long at)
    {
        if (_state == State.Idle)
        {
            _state = State.Pressed;
            _pressedAt = at;
            return HoldResult.None;
        }

        return Tick(at); // auto-repeat
    }

    private HoldResult TriggerUp(long at)
    {
        var wasTalking = _state == State.Talking;
        _state = State.Idle;
        if (!wasTalking)
        {
            return HoldResult.None;
        }

        return at - _pressedAt >= minHoldMs ? HoldResult.Stopped : HoldResult.Discarded;
    }

    private HoldResult OtherKeyDown()
    {
        switch (_state)
        {
            case State.Pressed:
                _state = State.Shortcut;
                return HoldResult.None;
            case State.Talking:
                _state = State.Shortcut;
                return HoldResult.Cancelled;
            default:
                return HoldResult.None;
        }
    }
}
