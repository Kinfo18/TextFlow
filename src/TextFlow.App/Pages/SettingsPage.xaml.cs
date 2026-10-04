using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace TextFlow.App.Pages;

/// <summary>Configuración: theme, start with Windows (H3.1) and the expansion chime (H4.2); hotkey and exclusions come in H4.3.</summary>
public sealed partial class SettingsPage : Page
{
    private const double SliderScale = 100; // the slider shows 0-100, settings keep 0-1
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        // The slider marks pointer events handled while dragging: listen to them anyway.
        VolumeSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnVolumeReleased), handledEventsToo: true);
        // A sleeping audio device swallows the first sound: wake it while the user heads for these controls.
        Loaded += (_, _) => App.Current.WarmSound();
        SoundCard.PointerEntered += (_, _) => App.Current.WarmSound();
        SoundCard.GotFocus += (_, _) => App.Current.WarmSound();
        ThemeChoice.SelectedIndex = (int)App.Current.Theme;
        StartWithWindowsSwitch.IsOn = App.Current.StartsWithWindows;
        SoundSwitch.IsOn = App.Current.SoundEnabled;
        VolumeSlider.Value = Math.Round(App.Current.ChimeVolume * SliderScale);
        UpdateSoundControls();
        _loading = false;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ThemeChoice.SelectedIndex >= 0)
        {
            App.Current.SetTheme((AppTheme)ThemeChoice.SelectedIndex);
        }
    }

    private void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            App.Current.SetStartWithWindows(StartWithWindowsSwitch.IsOn);
        }
    }

    private void OnSoundToggled(object sender, RoutedEventArgs e)
    {
        UpdateSoundControls();
        if (!_loading)
        {
            ApplySound();
        }
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loading)
        {
            ApplySound();
        }
    }

    // Let the user hear the new level once they let go, not on every step of the drag.
    private void OnVolumeReleased(object sender, PointerRoutedEventArgs e) => PlayPreview();

    private void OnVolumeKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown)
        {
            PlayPreview();
        }
    }

    private void OnTestSound(object sender, RoutedEventArgs e) => PlayPreview();

    private void ApplySound() => App.Current.SetSound(SoundSwitch.IsOn, VolumeSlider.Value / SliderScale);

    private void PlayPreview()
    {
        if (!SoundSwitch.IsOn)
        {
            return;
        }

        var played = VolumeSlider.Value <= 0 || App.Current.PlayChime();
        SoundStatus.Text = played ? string.Empty : "Windows no pudo reproducir el sonido. ¿Hay un dispositivo de audio activo?";
        SoundStatus.Visibility = played ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSoundControls()
    {
        VolumeSlider.IsEnabled = SoundSwitch.IsOn;
        TestSoundButton.IsEnabled = SoundSwitch.IsOn;
        if (!SoundSwitch.IsOn)
        {
            SoundStatus.Visibility = Visibility.Collapsed;
        }
    }
}
