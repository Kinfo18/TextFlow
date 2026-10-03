using System.Globalization;

namespace TextFlow.Core.Input;

/// <summary>Same bit values as Win32 MOD_*, so they pass straight to RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>
/// A global shortcut such as "Ctrl+Shift+Alt+P", stored as text in settings. Requires Ctrl, Alt or Win:
/// a bare key or Shift+key would swallow normal typing in every app.
/// </summary>
/// <param name="VirtualKey">Windows virtual-key code (A-Z, 0-9, F1-F24, Pause).</param>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private const uint PauseKey = 0x13;
    private const uint F1 = 0x70;
    private const int MaxFunctionKey = 24;

    /// <summary>Default pause/resume shortcut (H1.4). Not Ctrl+Alt+letter: that is AltGr on Spanish keyboards.</summary>
    public static HotkeyGesture DefaultPause { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt, 'P');

    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        uint? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (ParseModifier(part) is { } modifier)
            {
                modifiers |= modifier;
            }
            else if (key is null && ParseKey(part) is { } parsed)
            {
                key = parsed;
            }
            else
            {
                return false; // unknown token or a second key
            }
        }

        if (key is null || (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key.Value);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    private static HotkeyModifiers? ParseModifier(string part) => part.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => HotkeyModifiers.Control,
        "SHIFT" or "MAYÚS" or "MAYUS" => HotkeyModifiers.Shift,
        "ALT" => HotkeyModifiers.Alt,
        "WIN" or "WINDOWS" => HotkeyModifiers.Win,
        _ => null,
    };

    private static uint? ParseKey(string part)
    {
        var upper = part.ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return upper[0];
        }

        if (upper is "PAUSE" or "PAUSA")
        {
            return PauseKey;
        }

        if (upper.Length > 1 && upper[0] == 'F'
            && int.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= MaxFunctionKey)
        {
            return F1 + (uint)(number - 1);
        }

        return null;
    }

    private static string KeyName(uint key) => key switch
    {
        PauseKey => "Pause",
        >= F1 and < F1 + MaxFunctionKey => $"F{key - F1 + 1}",
        _ => ((char)key).ToString(),
    };
}
