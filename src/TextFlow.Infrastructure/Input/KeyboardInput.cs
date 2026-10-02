using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace TextFlow.Infrastructure.Input;

/// <summary>SendInput helpers. Every injected event carries <see cref="Signature"/> so our own hook ignores it.</summary>
internal static class KeyboardInput
{
    /// <summary>dwExtraInfo marker ("TFLW"). Other injectors (on-screen keyboard, AutoHotkey) are not ignored.</summary>
    public const nuint Signature = 0x54464C57;

    private static readonly VIRTUAL_KEY[] Modifiers =
    [
        VIRTUAL_KEY.VK_LSHIFT, VIRTUAL_KEY.VK_RSHIFT,
        VIRTUAL_KEY.VK_LCONTROL, VIRTUAL_KEY.VK_RCONTROL,
        VIRTUAL_KEY.VK_LMENU, VIRTUAL_KEY.VK_RMENU,
        VIRTUAL_KEY.VK_LWIN, VIRTUAL_KEY.VK_RWIN,
    ];

    private static readonly TimeSpan ModifierPollInterval = TimeSpan.FromMilliseconds(10);

    public static bool AnyModifierDown() =>
        Modifiers.Any(vk => (PInvoke.GetAsyncKeyState((int)vk) & 0x8000) != 0);

    /// <summary>
    /// Physically held modifiers would combine with injected keys (Ctrl+held Shift+V, Shift+Backspace...).
    /// We wait instead of injecting fake key-ups, which would desync the user's real keyboard state.
    /// </summary>
    public static async Task<bool> WaitForModifiersReleasedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (AnyModifierDown())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(ModifierPollInterval, ct).ConfigureAwait(false);
        }

        return true;
    }

    public static int Tap(VIRTUAL_KEY key, int count = 1)
    {
        if (count <= 0)
        {
            return 0;
        }

        var inputs = new INPUT[count * 2];
        for (var i = 0; i < count; i++)
        {
            inputs[i * 2] = VirtualKey(key, keyUp: false);
            inputs[(i * 2) + 1] = VirtualKey(key, keyUp: true);
        }

        return Send(inputs);
    }

    public static int Chord(VIRTUAL_KEY modifier, VIRTUAL_KEY key) => Send(
    [
        VirtualKey(modifier, keyUp: false),
        VirtualKey(key, keyUp: false),
        VirtualKey(key, keyUp: true),
        VirtualKey(modifier, keyUp: true),
    ]);

    /// <summary>Types text as Unicode packets; newlines and tabs become real Enter/Tab keys.</summary>
    /// <returns>Number of events accepted, and the total expected.</returns>
    public static (int Sent, int Expected) TypeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                continue; // CRLF: the following \n produces the Enter
            }

            if (c is '\n' or '\t')
            {
                var vk = c == '\n' ? VIRTUAL_KEY.VK_RETURN : VIRTUAL_KEY.VK_TAB;
                inputs.Add(VirtualKey(vk, keyUp: false));
                inputs.Add(VirtualKey(vk, keyUp: true));
                continue;
            }

            inputs.Add(Unicode(c, keyUp: false));
            inputs.Add(Unicode(c, keyUp: true));
        }

        return (Send(inputs.ToArray()), inputs.Count);
    }

    private static unsafe int Send(INPUT[] inputs)
    {
        if (inputs.Length == 0)
        {
            return 0;
        }

        fixed (INPUT* ptr = inputs)
        {
            return (int)PInvoke.SendInput((uint)inputs.Length, ptr, sizeof(INPUT));
        }
    }

    private static INPUT VirtualKey(VIRTUAL_KEY key, bool keyUp) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT
            {
                wVk = key,
                dwFlags = keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
                dwExtraInfo = Signature,
            },
        },
    };

    private static INPUT Unicode(char c, bool keyUp) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT
            {
                wScan = c,
                dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0),
                dwExtraInfo = Signature,
            },
        },
    };
}
