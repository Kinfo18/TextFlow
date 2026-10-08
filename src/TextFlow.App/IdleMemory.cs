using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace TextFlow.App;

/// <summary>
/// Gives memory back when TextFlow goes back to the tray (V0.1 criterion: idle RAM &lt; 150 MB): a full compacting
/// collection, then the working set is trimmed as Windows does with minimized apps. Pages that are needed again come
/// back from the standby list on first touch.
/// </summary>
internal static partial class IdleMemory
{
    /// <summary>Lets the closed window's XAML tree finish tearing down before collecting it.</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    private static DispatcherQueueTimer? _timer;

    public static void ReleaseSoon(DispatcherQueue ui)
    {
        _timer ??= CreateTimer(ui);
        _timer.Stop();
        _timer.Start();
    }

    private static DispatcherQueueTimer CreateTimer(DispatcherQueue ui)
    {
        var timer = ui.CreateTimer();
        timer.Interval = Delay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Release();
        return timer;
    }

    private static void Release()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);
}
