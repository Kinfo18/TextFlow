using Microsoft.Extensions.Time.Testing;
using TextFlow.Core.Input;

namespace TextFlow.Core.Tests.Input;

public sealed class HookWatchdogTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeReinstallableHook _hook = new();
    private uint _lastSystemInput;
    private bool _foregroundBlocksHook;
    private readonly HookWatchdog _watchdog;

    public HookWatchdogTests()
    {
        _watchdog = new HookWatchdog(_hook, () => _lastSystemInput, () => _foregroundBlocksHook, _time, HookWatchdogOptions.Default);
    }

    public void Dispose() => _watchdog.Dispose();

    [Fact]
    public async Task HealthyHook_SeesEveryInput_IsLeftAlone()
    {
        _lastSystemInput = 10_000;
        _hook.LastCallbackTick = 10_000;

        await _watchdog.CheckAsync();

        Assert.Equal(0, _hook.Reinstalls);
    }

    /// <summary>A dead hook: the system keeps seeing input over two checks, the hook sees none.</summary>
    private async Task DeadHookOverTwoChecksAsync()
    {
        _hook.LastCallbackTick = 10_000;
        _lastSystemInput = 15_000;
        await _watchdog.CheckAsync();
        _lastSystemInput = 17_000;
        await _watchdog.CheckAsync();
    }

    [Fact]
    public async Task InputTheHookNeverSaw_OnTwoChecks_ReinstallsIt_AndReportsTheCount()
    {
        var reported = new List<HookReinstall>();
        _watchdog.Reinstalled += reported.Add;

        await DeadHookOverTwoChecksAsync(); // Windows removed the hook: typing goes on without callbacks

        Assert.Equal(1, _hook.Reinstalls);
        Assert.Equal([new HookReinstall(1, 7_000)], reported);
    }

    [Fact]
    public async Task ASingleGap_IsNotEnough_TouchpadContactUpdatesTheSystemInputWithoutEvents()
    {
        _hook.LastCallbackTick = 10_000;
        _lastSystemInput = 11_800;

        await _watchdog.CheckAsync();

        Assert.Equal(0, _hook.Reinstalls);
    }

    [Fact]
    public async Task GapThatHealsBeforeTheSecondCheck_IsNotAFailure()
    {
        _hook.LastCallbackTick = 10_000;
        _lastSystemInput = 11_800;
        await _watchdog.CheckAsync();

        _hook.LastCallbackTick = 12_500; // the user typed: the hook is alive
        _lastSystemInput = 14_000;
        await _watchdog.CheckAsync();

        Assert.Equal(0, _hook.Reinstalls);
    }

    [Fact]
    public async Task NoNewSystemInput_IsNotConfirmation()
    {
        _hook.LastCallbackTick = 10_000;
        _lastSystemInput = 11_800;
        await _watchdog.CheckAsync();
        await _watchdog.CheckAsync(); // same stale gap, nothing new happened

        Assert.Equal(0, _hook.Reinstalls);
    }

    [Fact]
    public async Task InputWithinTheGrace_IsNotAFailure()
    {
        _hook.LastCallbackTick = 10_000;
        _lastSystemInput = 10_500;

        await _watchdog.CheckAsync();

        Assert.Equal(0, _hook.Reinstalls);
    }

    [Fact]
    public async Task ElevatedOrSecureForeground_IsSkipped_BecauseHooksCannotSeeItsInput()
    {
        _foregroundBlocksHook = true;

        await DeadHookOverTwoChecksAsync();

        Assert.Equal(0, _hook.Reinstalls);
    }

    [Fact]
    public async Task Reinstalls_AreThrottled()
    {
        _hook.ReinstallLeavesItDead = true;

        await DeadHookOverTwoChecksAsync();
        _time.Advance(TimeSpan.FromSeconds(5));
        _lastSystemInput = 19_000;
        await _watchdog.CheckAsync();
        _lastSystemInput = 21_000;
        await _watchdog.CheckAsync();
        Assert.Equal(1, _hook.Reinstalls);

        _time.Advance(HookWatchdogOptions.Default.MinReinstallGap);
        _lastSystemInput = 23_000;
        await _watchdog.CheckAsync();
        _lastSystemInput = 25_000;
        await _watchdog.CheckAsync();
        Assert.Equal(2, _hook.Reinstalls);
    }

    [Fact]
    public async Task FailedReinstall_IsReported_NotThrown()
    {
        Exception? failure = null;
        _watchdog.ReinstallFailed += ex => failure = ex;
        _hook.ReinstallThrows = true;

        await DeadHookOverTwoChecksAsync();

        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public async Task TickCounterWrapAround_IsHandled()
    {
        _hook.LastCallbackTick = uint.MaxValue - 100; // GetTickCount wraps every 49.7 days
        _lastSystemInput = 50;

        await _watchdog.CheckAsync();

        Assert.Equal(0, _hook.Reinstalls); // 150 ms apart, not 49 days
    }

    [Fact]
    public async Task Timer_RunsTheCheckPeriodically()
    {
        _hook.LastCallbackTick = 10_000;
        _lastSystemInput = 15_000;
        _time.Advance(HookWatchdogOptions.Default.Interval);
        await _watchdog.IdleAsync();

        _lastSystemInput = 17_000;
        _time.Advance(HookWatchdogOptions.Default.Interval);
        await _watchdog.IdleAsync();

        Assert.Equal(1, _hook.Reinstalls);
    }

    private sealed class FakeReinstallableHook : IReinstallableHook
    {
        public uint LastCallbackTick { get; set; }

        public int Reinstalls { get; private set; }

        public bool ReinstallLeavesItDead { get; set; }

        public bool ReinstallThrows { get; set; }

        public Task ReinstallAsync()
        {
            Reinstalls++;
            if (ReinstallThrows)
            {
                throw new InvalidOperationException("SetWindowsHookEx failed");
            }

            if (!ReinstallLeavesItDead)
            {
                LastCallbackTick = uint.MaxValue / 2; // a fresh hook counts as alive "now"
            }

            return Task.CompletedTask;
        }
    }
}
