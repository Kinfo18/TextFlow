using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TextFlow.App.Pages;

/// <summary>Configuración (H3.1: theme and start with Windows; sound, hotkey and exclusions come in H4).</summary>
public sealed partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeChoice.SelectedIndex = (int)App.Current.Theme;
        StartWithWindowsSwitch.IsOn = App.Current.StartsWithWindows;
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
}
