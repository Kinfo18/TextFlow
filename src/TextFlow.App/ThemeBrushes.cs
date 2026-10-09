using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TextFlow.App;

/// <summary>
/// Theme brushes for elements built in code. <c>Application.Current.Resources[key]</c> answers with the Windows
/// theme, not TextFlow's: with Windows dark and TextFlow light it returned the dark-theme yellow, green and pink,
/// unreadable on the light background. This looks the key up in the theme TextFlow actually shows.
/// </summary>
internal static class ThemeBrushes
{
    public static Brush Get(string key)
    {
        var resources = Application.Current.Resources;
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            foreach (var themeKey in ThemeKeys())
            {
                foreach (var dictionary in resources.MergedDictionaries.Prepend(resources))
                {
                    if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var theme)
                        && theme is ResourceDictionary themed
                        && themed.TryGetValue(key, out var value)
                        && value is Brush brush)
                    {
                        return brush;
                    }
                }
            }
        }

        return (Brush)resources[key]; // high contrast follows Windows, as the rest of the UI does
    }

    private static string[] ThemeKeys() => EffectiveTheme() == ApplicationTheme.Light ? ["Light"] : ["Dark", "Default"];

    private static ApplicationTheme EffectiveTheme() => App.Current.Theme switch
    {
        AppTheme.Light => ApplicationTheme.Light,
        AppTheme.Dark => ApplicationTheme.Dark,
        _ => Application.Current.RequestedTheme,
    };
}
