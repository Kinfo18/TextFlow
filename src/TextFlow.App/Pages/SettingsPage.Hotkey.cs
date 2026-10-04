using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TextFlow.Core.Input;
using Windows.System;
using Windows.UI.Core;

namespace TextFlow.App.Pages;

/// <summary>Pause shortcut recorder and prefix wait (H4.3).</summary>
public sealed partial class SettingsPage
{
    private bool _recording;

    private static string CurrentGesture => App.Current.PauseHotkey?.Gesture ?? HotkeyGesture.DefaultPause.ToString();

    private void LoadHotkey()
    {
        HotkeyText.Text = CurrentGesture;
        PreviewKeyDown += OnPagePreviewKeyDown;
    }

    private void OnRecordHotkey(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            StopRecording(null);
            return;
        }

        _recording = true;
        HotkeyText.Text = "Pulsa la combinación…";
        RecordHotkeyButton.Content = "Cancelar";
        ShowHotkeyStatus("Usa Ctrl, Alt o Win con una letra, un número, F1-F24 o Pausa. Esc cancela.");
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        StopRecording(null);
        TryApply(HotkeyGesture.DefaultPause);
    }

    private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        e.Handled = true; // while recording no key reaches buttons, access keys or the Alt menu
        if (e.Key == VirtualKey.Escape)
        {
            StopRecording(null);
            return;
        }

        if (IsModifier(e.Key))
        {
            return; // wait for the main key
        }

        var gesture = new HotkeyGesture(CurrentModifiers(), (uint)e.Key);
        if (!HotkeyGesture.TryParse(gesture.ToString(), out var valid) || valid != gesture)
        {
            ShowHotkeyStatus("Esa combinación no sirve: añade Ctrl, Alt o Win y usa una letra, un número, F1-F24 o Pausa.");
            return;
        }

        if (gesture.OverlapsAltGr)
        {
            ShowHotkeyStatus($"{gesture} es AltGr en tu teclado (por ejemplo AltGr+2 = @): te impediría escribir esos caracteres. Añade Shift o usa otra tecla.");
            return;
        }

        StopRecording(gesture);
    }

    private void StopRecording(HotkeyGesture? gesture)
    {
        _recording = false;
        RecordHotkeyButton.Content = "Cambiar";
        HotkeyText.Text = CurrentGesture;
        HideHotkeyStatus();
        if (gesture is not null)
        {
            TryApply(gesture);
        }
    }

    private void TryApply(HotkeyGesture gesture)
    {
        var applied = App.Current.SetPauseHotkey(gesture);
        HotkeyText.Text = CurrentGesture;
        if (applied)
        {
            HideHotkeyStatus();
        }
        else
        {
            ShowHotkeyStatus($"Otra aplicación ya usa {gesture}. Se mantiene el atajo anterior.");
        }
    }

    private void ShowHotkeyStatus(string text)
    {
        HotkeyStatus.Text = text;
        HotkeyStatus.Visibility = Visibility.Visible;
    }

    private void HideHotkeyStatus() => HotkeyStatus.Visibility = Visibility.Collapsed;

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
