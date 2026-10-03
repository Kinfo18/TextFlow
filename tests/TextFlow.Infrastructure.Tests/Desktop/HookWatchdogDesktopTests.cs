using System.Runtime.InteropServices;
using TextFlow.Core.Expansion;
using TextFlow.Core.Input;
using TextFlow.Infrastructure.Hooks;

namespace TextFlow.Infrastructure.Tests.Desktop;

/// <summary>
/// H1.6 / risk R3 with the real hook: it is removed behind its back (as Windows does after a callback timeout),
/// real input arrives, and the watchdog must notice and install it again. Moves the mouse by one pixel and back.
/// </summary>
[Trait("Category", "Desktop")]
[Collection(DesktopTestGroup.Name)]
public sealed partial class HookWatchdogDesktopTests
{
    private const uint MouseMove = 0x0001;

    [Fact]
    public async Task SilentlyRemovedHook_IsDetected_AndReinstalled()
    {
        using var hook = new KeyboardHook(new TriggerMatcher([], TriggerOptions.Default));
        using var watchdog = new HookWatchdog(hook, InputProbe.LastInputTick, () => false, TimeProvider.System, HookWatchdogOptions.Default);
        var reinstalls = 0;
        watchdog.Reinstalled += info => reinstalls = info.TimesThisSession;

        await hook.SimulateSilentRemovalAsync();
        await Task.Delay(1_500);
        await NudgeMouseAsync(); // input the dead hook cannot see

        await watchdog.CheckAsync();
        Assert.Equal(1, reinstalls);

        await Task.Delay(100);
        await NudgeMouseAsync();
        Assert.True(
            Math.Abs(unchecked((int)(InputProbe.LastInputTick() - hook.LastCallbackTick))) < 500,
            "the reinstalled hook must see new input");
    }

    [Fact]
    public async Task HealthyHook_IsNotReinstalled()
    {
        using var hook = new KeyboardHook(new TriggerMatcher([], TriggerOptions.Default));
        using var watchdog = new HookWatchdog(hook, InputProbe.LastInputTick, () => false, TimeProvider.System, HookWatchdogOptions.Default);
        var reinstalls = 0;
        watchdog.Reinstalled += info => reinstalls = info.TimesThisSession;

        await Task.Delay(1_500);
        await NudgeMouseAsync();
        await watchdog.CheckAsync();

        Assert.Equal(0, reinstalls);
    }

    private static async Task NudgeMouseAsync()
    {
        mouse_event(MouseMove, 1, 0, 0, 0);
        await Task.Delay(30);
        mouse_event(MouseMove, unchecked((uint)-1), 0, 0, 0);
        await Task.Delay(150); // let the hook thread run the callback
    }

    [LibraryImport("user32.dll")]
    private static partial void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extraInfo);
}
