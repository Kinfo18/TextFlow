using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using TextFlow.Core.Input;

namespace TextFlow.Spikes;

/// <summary>
/// V0.2 H0.4: what the low-level hook sees for the dictation key on the user's real keyboard (Right Ctrl, AltGr on the
/// Spanish layout, laptop keys such as Copilot/Fn), and what <see cref="HoldToTalk"/> would decide. Run it in a terminal
/// of its own: the output is live. Prints names of modifiers and special keys only; letters, digits and punctuation
/// show as "tecla", never their value. Usage: <c>keys [segundos] [rctrl|lctrl|f1..f24|0xVK] [ms]</c>.
/// </summary>
internal static partial class KeyProbe
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const uint FlagExtended = 0x01;
    private const uint FlagInjected = 0x10;
    private const uint FlagUp = 0x80;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;

    private delegate nint LowLevelProc(int code, nint wParam, nint lParam);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static LowLevelProc? s_keys;
    private static LowLevelProc? s_mouse;
    private static HoldToTalk s_hold = new(0xA3);
    private static long s_startedAt;
    private static readonly HashSet<uint> Held = []; // hook thread only: hide auto-repeat lines

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    public static int Run(string[] args)
    {
        var seconds = args.Length > 0 && int.TryParse(args[0], CultureInfo.InvariantCulture, out var s) ? s : 60;
        var trigger = args.Length > 1 ? ParseKey(args[1]) : 0xA3u;
        var holdMs = args.Length > 2 && int.TryParse(args[2], CultureInfo.InvariantCulture, out var h) ? h : HoldToTalk.DefaultHoldMs;
        s_hold = new HoldToTalk(trigger, holdMs);

        Console.CancelKeyPress += (_, e) => e.Cancel = true; // Ctrl+C is one of the tests: it must not end the probe
        s_keys = OnKey;
        s_mouse = OnMouse;
        var keyHook = SetWindowsHookEx(WhKeyboardLl, s_keys, GetModuleHandle(null), 0);
        var mouseHook = SetWindowsHookEx(WhMouseLl, s_mouse, GetModuleHandle(null), 0);
        Console.WriteLine($"Tecla de dictado: {Name(trigger)} (mantener {holdMs} ms). Escuchando {seconds} s; no se guarda nada.");
        Console.WriteLine("Prueba: mantener Ctrl derecho · toque corto · Ctrl derecho + C · AltGr + 2 (@) · teclas raras del portátil.\n");

        using var tick = new System.Windows.Forms.Timer { Interval = 20 };
        tick.Tick += (_, _) => Report(s_hold.Tick(Clock.ElapsedMilliseconds), "(mantenida)");
        using var stop = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
        stop.Tick += (_, _) => Application.ExitThread();
        tick.Start();
        stop.Start();
        Application.Run();

        UnhookWindowsHookEx(keyHook);
        UnhookWindowsHookEx(mouseHook);
        return 0;
    }

    private static nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var down = (k.Flags & FlagUp) == 0;
            var injected = (k.Flags & FlagInjected) != 0;
            var flags = ((k.Flags & FlagExtended) != 0 ? "E" : "") + (injected ? "I" : "");
            var result = s_hold.Feed(new KeyStroke(k.VkCode, k.ScanCode, down, injected, Clock.ElapsedMilliseconds));
            var repeat = down ? !Held.Add(k.VkCode) : !Held.Remove(k.VkCode);
            if (!repeat)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{Clock.ElapsedMilliseconds,8} ms  {(down ? "↓" : "↑")} {Name(k.VkCode),-14} scan=0x{k.ScanCode:X3} {flags,-2}"));
            }

            Report(result, "");
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && wParam is WmLButtonDown or WmRButtonDown or WmMButtonDown)
        {
            s_hold.Interrupt();
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static void Report(HoldResult result, string why)
    {
        switch (result)
        {
            case HoldResult.Started:
                s_startedAt = Clock.ElapsedMilliseconds;
                Console.WriteLine($"          >>> DICTADO EMPIEZA {why}");
                break;
            case HoldResult.Stopped:
                Console.WriteLine($"          <<< DICTADO TERMINA tras {Clock.ElapsedMilliseconds - s_startedAt} ms: se insertaría");
                break;
            case HoldResult.Discarded:
                Console.WriteLine($"          ··· toque corto ({Clock.ElapsedMilliseconds - s_startedAt} ms grabando): se descarta");
                break;
            case HoldResult.Cancelled:
                Console.WriteLine("          xxx DICTADO CANCELADO (otra tecla): no se inserta nada");
                break;
        }
    }

    private static uint ParseKey(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        if (t == "RCTRL") return 0xA3;
        if (t == "LCTRL") return 0xA2;
        if (t.StartsWith('F') && int.TryParse(t[1..], CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24) return (uint)(0x6F + f);
        if (t.StartsWith("0X", StringComparison.Ordinal)) return uint.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        throw new ArgumentException($"Tecla desconocida: {text}");
    }

    /// <summary>Modifiers and special keys by name; anything that types a character stays anonymous.</summary>
    private static string Name(uint vk) => vk switch
    {
        0xA0 => "Shift izq",
        0xA1 => "Shift der",
        0xA2 => "Ctrl izq",
        0xA3 => "Ctrl der",
        0xA4 => "Alt izq",
        0xA5 => "Alt der/AltGr",
        0x5B => "Win izq",
        0x5C => "Win der",
        0x5D => "Menú (Apps)",
        0x14 => "Bloq Mayús",
        0x1B => "Esc",
        0x0D => "Enter",
        0x20 => "Espacio",
        0x08 => "Retroceso",
        0x09 => "Tab",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        >= 0x21 and <= 0x2E => $"nav 0x{vk:X2}",
        >= 0xA6 and <= 0xB7 => $"media 0x{vk:X2}",
        >= 0x30 and <= 0x5A or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xE2 => "tecla",
        _ => $"VK 0x{vk:X2}",
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, LowLevelProc proc, nint module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
}
