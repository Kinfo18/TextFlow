using TextFlow.Core.Input;

namespace TextFlow.Core.Tests.Input;

public sealed class HotkeyGestureTests
{
    [Fact]
    public void Default_IsCtrlShiftAltP()
    {
        Assert.Equal("Ctrl+Shift+Alt+P", HotkeyGesture.DefaultPause.ToString());
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt, HotkeyGesture.DefaultPause.Modifiers);
        Assert.Equal((uint)'P', HotkeyGesture.DefaultPause.VirtualKey);
    }

    [Theory]
    [InlineData("Ctrl+Shift+Alt+P", "Ctrl+Shift+Alt+P")]
    [InlineData("alt + ctrl + shift + p", "Ctrl+Shift+Alt+P")] // order and case do not matter
    [InlineData("Win+Alt+T", "Alt+Win+T")]
    [InlineData("Ctrl+Alt+Pause", "Ctrl+Alt+Pause")]
    [InlineData("Ctrl+Alt+Pausa", "Ctrl+Alt+Pause")]
    [InlineData("Ctrl+F12", "Ctrl+F12")]
    [InlineData("Ctrl+Shift+7", "Ctrl+Shift+7")]
    public void Parse_RoundTrips(string text, string expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(expected, gesture!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("P")]                 // no modifier: would steal a normal key
    [InlineData("Shift+P")]           // Shift alone only changes case
    [InlineData("Ctrl+Shift")]        // no key
    [InlineData("Ctrl+P+Q")]          // two keys
    [InlineData("Ctrl+Banana")]
    public void Parse_RejectsUnsafeOrInvalidGestures(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void FunctionKeys_MapToTheirVirtualKeys()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+F1", out var f1));
        Assert.Equal(0x70u, f1!.VirtualKey);
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Pause", out var pause));
        Assert.Equal(0x13u, pause!.VirtualKey);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Q", true)]   // AltGr+Q = @ on a Spanish keyboard
    [InlineData("Ctrl+Alt+2", true)]
    [InlineData("Ctrl+Shift+Alt+P", false)]
    [InlineData("Ctrl+Alt+F5", false)]
    [InlineData("Ctrl+Alt+Pause", false)]
    [InlineData("Win+Alt+Q", false)]
    [InlineData("Ctrl+Shift+P", false)]
    public void OverlapsAltGr_WhenItIsCtrlAltPlusACharacterKey(string text, bool expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(expected, gesture!.OverlapsAltGr);
    }
}
