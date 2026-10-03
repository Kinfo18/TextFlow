using TextFlow.Core.Input;
using TextFlow.Infrastructure.Windows;

namespace TextFlow.Infrastructure.Tests.Windows;

/// <summary>Registers a gesture nobody uses (no key is pressed, so these tests do not touch the desktop).</summary>
public sealed class GlobalHotkeyTests
{
    private static readonly HotkeyGesture Unused =
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Win, 0x86); // F23

    [Fact]
    public void Register_Succeeds_ForAFreeGesture()
    {
        using var hotkey = new GlobalHotkey(Unused);

        Assert.True(hotkey.IsRegistered);
    }

    [Fact]
    public void Register_Fails_WhenTheGestureIsAlreadyTaken()
    {
        using var first = new GlobalHotkey(Unused);
        using var second = new GlobalHotkey(Unused);

        Assert.True(first.IsRegistered);
        Assert.False(second.IsRegistered);
    }

    [Fact]
    public void Dispose_ReleasesTheGesture()
    {
        new GlobalHotkey(Unused).Dispose();

        using var again = new GlobalHotkey(Unused);

        Assert.True(again.IsRegistered);
    }
}
