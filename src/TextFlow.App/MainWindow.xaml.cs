using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TextFlow.App.Pages;
using Windows.Graphics;

namespace TextFlow.App;

/// <summary>Main window shell (spec §23, H3.1): navigation between Inicio · Snippets · Configuración · Diagnóstico.</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// Pages are created and shown directly: Frame.Navigate(Type) needs XAML type metadata and crashed (access
    /// violation in coreclr) for the code-only placeholder pages.
    /// </summary>
    private static readonly Dictionary<string, Func<Page>> Pages = new()
    {
        ["home"] = () => new HomePage(),
        ["snippets"] = () => new SnippetsPage(),
        ["settings"] = () => new SettingsPage(),
        ["diagnostics"] = () => new DiagnosticsPage(),
    };

    private const int MinimumWidth = 900;
    private const int MinimumHeight = 560;

    private string? _current;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new SizeInt32(1100, 720));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Below this the three Snippets panes cannot all fit (tree, list and editor minimums plus margins).
            // The presenter works in physical pixels: scale the logical minimum (125 % on the dev laptop).
            var scale = Menu.PopupInterop.DpiScale(WinRT.Interop.WindowNative.GetWindowHandle(this));
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(MinimumWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinimumHeight * scale);
        }
        ApplyTheme(App.Current.Theme);
        Navigate("home");
    }

    /// <summary>Re-reads the engine state (pause, library) into the visible page.</summary>
    public void Refresh() => (ContentHost.Content as IRefreshable)?.Refresh();

    /// <summary>Light, dark or follow Windows; the caption buttons follow the content.</summary>
    public void ApplyTheme(AppTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            Navigate(tag);
        }
    }

    private void Navigate(string tag)
    {
        if (_current == tag || ContentHost is null)
        {
            return; // SelectionChanged also fires while InitializeComponent builds the NavigationView
        }

        _current = tag;
        ContentHost.Content = Pages[tag]();
    }
}
