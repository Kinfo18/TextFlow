using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TextFlow.Core.Input;
using Windows.System;
using Windows.UI.Core;

namespace TextFlow.App.Pages;

/// <summary>Shortcut recorder for pause (H4.3) and the command palette (D11), and the prefix wait.</summary>
public sealed partial class SettingsPage
{
    private enum Shortcut
    {
        Pause,
        Palette,
    }

    private Shortcut? _recording;

    private static string Gesture(Shortcut shortcut) => shortcut == Shortcut.Pause
        ? App.Current.PauseHotkey?.Gesture ?? HotkeyGesture.DefaultPause.ToString()
        : App.Current.PaletteHotkey?.Gesture ?? HotkeyGesture.DefaultPalette.ToString();

    private (TextBlock Text, Button Record, TextBlock Status) Controls(Shortcut shortcut) => shortcut == Shortcut.Pause
        ? (HotkeyText, RecordHotkeyButton, HotkeyStatus)
        : (PaletteHotkeyText, RecordPaletteButton, PaletteHotkeyStatus);

    private void LoadHotkey()
    {
        HotkeyText.Text = Gesture(Shortcut.Pause);
        PaletteHotkeyText.Text = Gesture(Shortcut.Palette);
        if (App.Current.PaletteHotkey is { Registered: false } palette)
        {
            ShowHotkeyStatus(Shortcut.Palette, $"Otra aplicación ya usa {palette.Gesture}: elige otro atajo para la paleta.");
        }

        PreviewKeyDown += OnPagePreviewKeyDown;
    }

    private void OnRecordHotkey(object sender, RoutedEventArgs e) => ToggleRecording(Shortcut.Pause);

    private void OnRecordPaletteHotkey(object sender, RoutedEventArgs e) => ToggleRecording(Shortcut.Palette);

    private void OnResetHotkey(object sender, RoutedEventArgs e) => Reset(Shortcut.Pause, HotkeyGesture.DefaultPause);

    private void OnResetPaletteHotkey(object sender, RoutedEventArgs e) => Reset(Shortcut.Palette, HotkeyGesture.DefaultPalette);

    private void ToggleRecording(Shortcut shortcut)
    {
        var wasRecording = _recording;
        if (wasRecording is { } current)
        {
            StopRecording(current, null);
        }

        if (wasRecording == shortcut)
        {
            return; // the button said "Cancelar"
        }

        _recording = shortcut;
        var (text, record, _) = Controls(shortcut);
        text.Text = "Pulsa la combinación…";
        record.Content = "Cancelar";
        ShowHotkeyStatus(shortcut, "Usa Ctrl, Alt o Win con una letra, un número, F1-F24, Pausa o Espacio. Esc cancela.");
    }

    private void Reset(Shortcut shortcut, HotkeyGesture gesture)
    {
        if (_recording is { } current)
        {
            StopRecording(current, null);
        }

        TryApply(shortcut, gesture);
    }

    private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_recording is not { } shortcut)
        {
            return;
        }

        e.Handled = true; // while recording no key reaches buttons, access keys or the Alt menu
        if (e.Key == VirtualKey.Escape)
        {
            StopRecording(shortcut, null);
            return;
        }

        if (IsModifier(e.Key))
        {
            return; // wait for the main key
        }

        var gesture = new HotkeyGesture(CurrentModifiers(), (uint)e.Key);
        if (!HotkeyGesture.TryParse(gesture.ToString(), out var valid) || valid != gesture)
        {
            ShowHotkeyStatus(shortcut, "Esa combinación no sirve: añade Ctrl, Alt o Win y usa una letra, un número, F1-F24, Pausa o Espacio.");
            return;
        }

        if (gesture.OverlapsAltGr)
        {
            ShowHotkeyStatus(shortcut, $"{gesture} es AltGr en tu teclado (por ejemplo AltGr+2 = @): te impediría escribir esos caracteres. Añade Shift o usa otra tecla.");
            return;
        }

        if (gesture.ToString() == Gesture(shortcut == Shortcut.Pause ? Shortcut.Palette : Shortcut.Pause))
        {
            ShowHotkeyStatus(shortcut, $"{gesture} ya es el otro atajo de TextFlow.");
            return;
        }

        StopRecording(shortcut, gesture);
    }

    private void StopRecording(Shortcut shortcut, HotkeyGesture? gesture)
    {
        _recording = null;
        var (text, record, _) = Controls(shortcut);
        record.Content = "Cambiar";
        text.Text = Gesture(shortcut);
        HideHotkeyStatus(shortcut);
        if (gesture is not null)
        {
            TryApply(shortcut, gesture);
        }
    }

    private void TryApply(Shortcut shortcut, HotkeyGesture gesture)
    {
        var applied = shortcut == Shortcut.Pause ? App.Current.SetPauseHotkey(gesture) : App.Current.SetPaletteHotkey(gesture);
        Controls(shortcut).Text.Text = Gesture(shortcut);
        if (applied)
        {
            HideHotkeyStatus(shortcut);
        }
        else
        {
            ShowHotkeyStatus(shortcut, $"Otra aplicación ya usa {gesture}. Se mantiene el atajo anterior.");
        }
    }

    private void ShowHotkeyStatus(Shortcut shortcut, string text)
    {
        var status = Controls(shortcut).Status;
        status.Text = text;
        status.Visibility = Visibility.Visible;
    }

    private void HideHotkeyStatus(Shortcut shortcut) => Controls(shortcut).Status.Visibility = Visibility.Collapsed;

    private static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnPrefixChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (PrefixHeader is null)
        {
            return; // Minimum=200 coerces the initial 0 inside InitializeComponent, before the header exists
        }

        UpdatePrefixHeader();
        if (!_loading)
        {
            App.Current.SetPrefixTimeout((int)PrefixSlider.Value);
        }
    }

    private void UpdatePrefixHeader() => PrefixHeader.Text = $"Espera para abreviaturas que empiezan otra: {PrefixSlider.Value:0} ms";
}
