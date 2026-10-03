using TextFlow.Infrastructure.Targeting;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace TextFlow.Infrastructure.Hooks;

/// <summary>System-wide input facts the hook watchdog compares against (no content, cheap Win32 calls).</summary>
public static class InputProbe
{
    private static readonly bool SelfElevated = Win32TargetResolver.IsProcessElevated((uint)Environment.ProcessId);

    /// <summary>GetLastInputInfo: tick of the last keyboard or mouse input anywhere in the session.</summary>
    public static unsafe uint LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)sizeof(LASTINPUTINFO) };
        return PInvoke.GetLastInputInfo(&info) ? info.dwTime : unchecked((uint)Environment.TickCount);
    }

    /// <summary>Process name of the foreground window (diagnostics), empty when there is none.</summary>
    public static unsafe string ForegroundProcessName()
    {
        var foreground = PInvoke.GetForegroundWindow();
        uint processId;
        return !foreground.IsNull && PInvoke.GetWindowThreadProcessId(foreground, &processId) != 0
            ? Win32TargetResolver.GetProcessName(processId)
            : string.Empty;
    }

    /// <summary>
    /// True when low-level hooks cannot see the input at all: the secure desktop (UAC, lock screen: no foreground
    /// window) or an elevated foreground window while TextFlow is not elevated (UIPI).
    /// </summary>
    public static unsafe bool ForegroundBlocksHooks()
    {
        var foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull)
        {
            return true;
        }

        uint processId;
        if (PInvoke.GetWindowThreadProcessId(foreground, &processId) == 0)
        {
            return true; // window vanished meanwhile: skip this round rather than reinstall needlessly
        }

        return !SelfElevated && Win32TargetResolver.IsProcessElevated(processId);
    }
}
