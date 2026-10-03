namespace TextFlow.Core.Input;

/// <summary>A low-level hook that can be installed again after Windows silently removed it.</summary>
public interface IReinstallableHook
{
    /// <summary>GetTickCount-style milliseconds of the last keyboard or mouse callback (any input, even filtered).</summary>
    uint LastCallbackTick { get; }

    /// <summary>Unhooks and hooks again on the hook thread.</summary>
    Task ReinstallAsync();
}

/// <param name="TimesThisSession">Reinstalls so far, this one included.</param>
/// <param name="MissedInputMs">How much newer the system's last input was than the hook's last callback.</param>
public sealed record HookReinstall(int TimesThisSession, int MissedInputMs);

/// <param name="Interval">How often to compare the hook against Windows' last input.</param>
/// <param name="MissedInputGrace">Input this much newer than the last callback means the hook missed it.</param>
/// <param name="MinReinstallGap">Never reinstall more often than this (a hook that keeps dying must not spin).</param>
public sealed record HookWatchdogOptions(TimeSpan Interval, TimeSpan MissedInputGrace, TimeSpan MinReinstallGap)
{
    public static HookWatchdogOptions Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
}

/// <summary>
/// Risk R3: Windows removes a low-level hook without notice when a callback exceeds LowLevelHooksTimeout,
/// and TextFlow would silently stop expanding. If the system saw input (GetLastInputInfo) that the hook never
/// saw, the hook is dead: install it again.
/// </summary>
public sealed class HookWatchdog : IDisposable
{
    private readonly IReinstallableHook _hook;
    private readonly Func<uint> _lastSystemInputTick;
    private readonly Func<bool> _foregroundBlocksHook;
    private readonly TimeProvider _time;
    private readonly HookWatchdogOptions _options;
    private readonly ITimer _timer;
    private int _checking;
    private DateTimeOffset? _lastReinstall;
    private int _reinstalls;
    private Task _lastCheck = Task.CompletedTask;

    /// <param name="lastSystemInputTick">GetLastInputInfo().dwTime.</param>
    /// <param name="foregroundBlocksHook">
    /// True when hooks cannot see the input anyway: an elevated foreground window (UIPI) or the secure desktop.
    /// </param>
    public HookWatchdog(
        IReinstallableHook hook,
        Func<uint> lastSystemInputTick,
        Func<bool> foregroundBlocksHook,
        TimeProvider time,
        HookWatchdogOptions options)
    {
        _hook = hook;
        _lastSystemInputTick = lastSystemInputTick;
        _foregroundBlocksHook = foregroundBlocksHook;
        _time = time;
        _options = options;
        _timer = time.CreateTimer(_ => _lastCheck = CheckAsync(), null, options.Interval, options.Interval);
    }

    /// <summary>Raised after each reinstall with how many happened this session.</summary>
    public event Action<HookReinstall>? Reinstalled;

    /// <summary>Raised when installing the hook again failed; the next attempt waits for <see cref="HookWatchdogOptions.MinReinstallGap"/>.</summary>
    public event Action<Exception>? ReinstallFailed;

    /// <summary>Completes when the check started by the last timer tick has finished (tests).</summary>
    public Task IdleAsync() => _lastCheck;

    public async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return; // a slow reinstall is still running
        }

        try
        {
            var missedMs = MissedInputMs();
            if (missedMs <= _options.MissedInputGrace.TotalMilliseconds || _foregroundBlocksHook() || Throttled())
            {
                return;
            }

            _lastReinstall = _time.GetUtcNow(); // throttle failures too
            try
            {
                await _hook.ReinstallAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
            {
                ReinstallFailed?.Invoke(ex);
                return;
            }

            Reinstalled?.Invoke(new HookReinstall(++_reinstalls, missedMs));
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>Unsigned difference, so the 49.7-day GetTickCount wrap does not look like a huge gap.</summary>
    private int MissedInputMs() => unchecked((int)(_lastSystemInputTick() - _hook.LastCallbackTick));

    private bool Throttled() => _lastReinstall is { } last && _time.GetUtcNow() - last < _options.MinReinstallGap;

    public void Dispose() => _timer.Dispose();
}
