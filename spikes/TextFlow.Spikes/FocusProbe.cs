using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TextFlow.Spikes;

/// <summary>
/// Investigation (2026-10-03): which EVENT_OBJECT_FOCUS events a page fires while the user types (WhatsApp Web in
/// Firefox broke triggers). Logs window class, object/child ids and key-down ticks only: never key values or text.
/// Usage: <c>focus [seconds] [output.csv]</c>.
/// </summary>
internal static partial class FocusProbe
{
    private const uint EventObjectFocus = 0x8005;
    private const uint WinEventOutOfContext = 0;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;

    private delegate void WinEventProc(nint hook, uint @event, nint hwnd, int idObject, int idChild, uint thread, uint time);

    private delegate nint LowLevelProc(int code, nint wParam, nint lParam);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<string> Lines = ["ms,kind,class,process,idObject,idChild"];
    private static WinEventProc? s_focus;
    private static LowLevelProc? s_keys;

    public static int Run(string[] args)
    {
        var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 20;
        var output = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "focus-probe.csv");

        s_focus = OnFocus;
        s_keys = OnKey;
        var focusHook = SetWinEventHook(EventObjectFocus, EventObjectFocus, 0, s_focus, 0, 0, WinEventOutOfContext);
        var keyHook = SetWindowsHookEx(WhKeyboardLl, s_keys, GetModuleHandle(null), 0);
        Console.WriteLine($"Registrando {seconds} s de eventos de foco (sin contenido). Escribe en la app a investigar…");

        using var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
        timer.Tick += (_, _) => Application.ExitThread();
        timer.Start();
        Application.Run();

        UnhookWinEvent(focusHook);
        UnhookWindowsHookEx(keyHook);
        File.WriteAllLines(output, Lines);
        Console.WriteLine($"{Lines.Count - 1} eventos → {output}");
        return 0;
    }

    private static void OnFocus(nint hook, uint @event, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var buffer = new char[128];
        var name = new string(buffer, 0, Math.Max(0, GetClassName(hwnd, buffer, buffer.Length)));
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try
        {
            process = Process.GetProcessById((int)pid).ProcessName;
        }
        catch (ArgumentException)
        {
            process = "?";
        }

        Lines.Add($"{Clock.ElapsedMilliseconds},focus,{name},{process},{idObject},{idChild}");
    }

    private static nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && wParam == WmKeyDown)
        {
            Lines.Add($"{Clock.ElapsedMilliseconds},key,,,,");
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, LowLevelProc proc, nint module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, [Out] char[] name, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
