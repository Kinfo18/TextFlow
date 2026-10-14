using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TextFlow.App.Pages;
using TextFlow.Core.Windowing;
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

    private const int MinimumWidth = 720;
    private const int MinimumHeight = 560;
    private static readonly SizeInt32 DefaultSize = new(1100, 720);
    private static readonly TimeSpan RevealFallback = TimeSpan.FromSeconds(1);

    private string? _current;
    private bool _confirmingClose;
    private RectInt32 _restoredBounds;

    public MainWindow()
    {
        InitializeComponent();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Menu.PopupInterop.Cloak(hwnd, true); // shown only once placed and painted (see RevealWhenPainted)
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        if (File.Exists(App.IconFile))
        {
            AppWindow.SetIcon(App.IconFile); // taskbar and Alt+Tab
        }

        var titleIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "TextFlow-64.png");
        if (File.Exists(titleIcon))
        {
            TitleBarIcon.Source = new BitmapImage(new Uri(titleIcon)); // the custom title bar draws its own
        }
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Below this the two Snippets columns cannot fit (tree and list/editor minimums, margins and the compact pane).
            // The presenter works in physical pixels: scale the logical minimum (125 % on the dev laptop).
            var scale = Menu.PopupInterop.DpiScale(hwnd);
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(MinimumWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinimumHeight * scale);
        }

        RestorePlacement(App.Current.WindowPlacement);
        AppWindow.Changed += OnAppWindowChanged;
        VersionText.Text = $"v{Updates.AppVersion.Display}";
        ApplyTheme(App.Current.Theme);
        Navigate("home");
        RevealWhenPainted(hwnd);
    }

    /// <summary>
    /// Uncloaks after the content's first frame: before this, opening showed a blank window for a moment and a
    /// maximized one first appeared restored, then jumped. A fallback uncloaks anyway if Loaded never comes.
    /// </summary>
    private void RevealWhenPainted(nint hwnd)
    {
        var revealed = false;
        void Reveal()
        {
            if (!revealed)
            {
                revealed = true;
                Menu.PopupInterop.Cloak(hwnd, false);
            }
        }

        if (Content is FrameworkElement root)
        {
            root.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Reveal);
        }

        var fallback = DispatcherQueue.CreateTimer();
        fallback.Interval = RevealFallback;
        fallback.IsRepeating = false;
        fallback.Tick += (_, _) => Reveal();
        fallback.Start();
    }

    /// <summary>Where the window is now, to reopen it there; null if it was never placed.</summary>
    public WindowPlacement? Placement =>
        _restoredBounds.Width <= 0 || _restoredBounds.Height <= 0
            ? null
            : new WindowPlacement(
                _restoredBounds.X, _restoredBounds.Y, _restoredBounds.Width, _restoredBounds.Height,
                AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized });

    /// <summary>Set once closing was confirmed, so the Close() that follows is not asked again.</summary>
    public bool CloseConfirmed { get; private set; }

    /// <summary>Closing destroys the pages: a modified command is saved, discarded or kept open first.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (_confirmingClose)
        {
            return false; // a second click on X while the dialog is open: WinUI allows only one ContentDialog
        }

        _confirmingClose = true;
        try
        {
            CloseConfirmed = ContentHost.Content is not SnippetsPage snippets || await snippets.ConfirmLeaveAsync();
            return CloseConfirmed;
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    /// <summary>Opens a section from inside a page ("diagnostics" from Inicio's numbers).</summary>
    public void ShowSection(string tag) =>
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => Equals(item.Tag, tag));

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

    /// <summary>The last place, pulled inside the nearest screen (a monitor may have been unplugged since).</summary>
    private void RestorePlacement(WindowPlacement? saved)
    {
        var fit = saved is null
            ? null
            : WindowBounds.Fit(
                new WindowRect(saved.X, saved.Y, saved.Width, saved.Height),
                ToPixels(DisplayArea.GetFromRect(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height), DisplayAreaFallback.Nearest).WorkArea));
        if (fit is not { } bounds)
        {
            AppWindow.Resize(DefaultSize);
        }
        else
        {
            var rect = new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            AppWindow.MoveAndResize(rect);
            AppWindow.MoveAndResize(rect); // landing on a monitor with another scale rescales the size once: apply it again
        }

        _restoredBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        if (saved?.Maximized == true && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private static WindowRect ToPixels(RectInt32 rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Remembers the bounds only while the window is neither maximized nor minimized, as Windows itself does.</summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if ((args.DidPositionChange || args.DidSizeChange)
            && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _restoredBounds = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
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
