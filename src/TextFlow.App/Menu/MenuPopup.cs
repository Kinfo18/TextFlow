using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TextFlow.Core.Menus;
using TextFlow.Infrastructure.Windows;
using Windows.Graphics;
using WinRT;

namespace TextFlow.App.Menu;

/// <summary>
/// Group menu window (ADR-0008): never activates, so the target keeps focus and caret. Breadcrumb header with
/// the app gradient, numbered rows (1-9), info entries in italics, › for subgroups, acrylic + rounded corners.
/// Keyboard input arrives from the hook through <see cref="WinUiMenuPresenter"/>; UI thread only.
/// </summary>
internal sealed partial class MenuPopup : Window
{
    public const int MaxVisibleRows = 12;
    private const double HeaderDip = 34;
    private const double RowDip = 36;
    private const double PaddingDip = 8;
    private const double MinWidthDip = 280;
    private const double MaxWidthDip = 560;
    private const double CharWidthDip = 7.2; // Segoe UI 14 px average, good enough to size the window

    private static readonly Windows.UI.Color Accent = ColorHelper.FromArgb(0xFF, 0x3B, 0x82, 0xF6);
    private static readonly SolidColorBrush SelectedBrush = new(ColorHelper.FromArgb(0x38, 0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush HoverBrush = new(ColorHelper.FromArgb(0x18, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush Transparent = new(Colors.Transparent);

    private readonly TextBlock _breadcrumb;
    private readonly StackPanel _rows;
    private readonly ScrollViewer _scroller;
    private readonly List<Border> _rowViews = [];
    private int _selected = -1;

    public MenuPopup()
    {
        _breadcrumb = new TextBlock
        {
            Margin = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.8,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var header = new Border
        {
            Height = HeaderDip,
            Child = _breadcrumb,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = ColorHelper.FromArgb(0x48, 0x3B, 0x82, 0xF6), Offset = 0 },
                    new GradientStop { Color = ColorHelper.FromArgb(0x20, 0x8B, 0x5C, 0xF6), Offset = 1 },
                },
            },
        };

        _rows = new StackPanel { Padding = new Thickness(4, 4, 4, 4) };
        _scroller = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var layout = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        Grid.SetRow(_scroller, 1);
        layout.Children.Add(header);
        layout.Children.Add(_scroller);
        Content = layout;

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ConfigureNonActivating();
        TryEnableAcrylic();
    }

    /// <summary>A row was clicked: index into the visible level's entries.</summary>
    public event Action<int>? RowActivated;

    public nint Hwnd { get; }

    /// <summary>
    /// A WinUI window renders nothing until activated once. Do that off-screen at startup (the only moment
    /// TextFlow takes focus), then hide; later shows never activate.
    /// </summary>
    public void Prime()
    {
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 10, 10));
        Activate();
        AppWindow.Hide();
    }

    /// <summary>Rebuilds the rows for <paramref name="state"/>'s visible level and returns the window size in physical pixels.</summary>
    public SizeInt32 Present(MenuState state, double scale)
    {
        var level = state.Current;
        _breadcrumb.Text = string.Join("  ›  ", state.Path.Select(p => p.Title));
        _rows.Children.Clear();
        _rowViews.Clear();

        for (var i = 0; i < level.Entries.Count; i++)
        {
            var row = CreateRow(level, i);
            _rowViews.Add(row);
            _rows.Children.Add(row);
        }

        if (level.Entries.Count == 0)
        {
            _rows.Children.Add(new TextBlock { Text = "(grupo vacío)", FontStyle = Windows.UI.Text.FontStyle.Italic, Opacity = 0.6, Margin = new Thickness(12, 8, 12, 8) });
        }

        _selected = -1;
        Select(level.Selected);

        var longest = level.Entries.Select(e => e.Label.Length).DefaultIfEmpty(0).Max();
        var width = Math.Clamp((longest * CharWidthDip) + 90, Math.Max(MinWidthDip, _breadcrumb.Text.Length * CharWidthDip), MaxWidthDip);
        var height = HeaderDip + (Math.Clamp(level.Entries.Count, 1, MaxVisibleRows) * RowDip) + PaddingDip + 2;
        return new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    /// <summary>Moves the highlight and keeps it in view.</summary>
    public void Select(int index)
    {
        if (_selected >= 0 && _selected < _rowViews.Count)
        {
            Highlight(_rowViews[_selected], selected: false);
        }

        _selected = index;
        if (index < 0 || index >= _rowViews.Count)
        {
            return;
        }

        Highlight(_rowViews[index], selected: true);
        var top = (index * RowDip) + 4;
        if (top < _scroller.VerticalOffset || top + RowDip > _scroller.VerticalOffset + _scroller.ViewportHeight)
        {
            _scroller.ChangeView(null, Math.Max(0, top - ((MaxVisibleRows / 2) * RowDip)), null, disableAnimation: true);
        }
    }

    public void ShowAt(RectInt32 bounds)
    {
        AppWindow.MoveAndResize(bounds);
        AppWindow.Show(activateWindow: false);
    }

    public void HidePopup() => AppWindow.Hide();

    private Border CreateRow(MenuLevel level, int index)
    {
        var entry = level.Entries[index];
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(28) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(22) },
            },
        };

        var accentBar = new Rectangle { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, Fill = new SolidColorBrush(Accent), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        grid.Children.Add(accentBar);

        var marker = level.NumberOf(entry) is { } number
            ? new TextBlock { Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture), Foreground = new SolidColorBrush(Accent), FontWeight = FontWeights.SemiBold }
            : new TextBlock { Text = entry.IsSelectable ? string.Empty : "ⓘ", Opacity = 0.55 };
        marker.HorizontalAlignment = HorizontalAlignment.Center;
        marker.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(marker);

        var label = new TextBlock
        {
            Text = entry.Label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0),
        };
        if (!entry.IsSelectable)
        {
            label.FontStyle = Windows.UI.Text.FontStyle.Italic;
            label.Opacity = 0.6;
        }

        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (entry is MenuGroupEntry)
        {
            var chevron = new FontIcon { Glyph = "", FontSize = 11, Opacity = 0.6 };
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
        }

        var row = new Border { Height = RowDip, CornerRadius = new CornerRadius(6), Child = grid, Background = Transparent, Tag = accentBar };
        if (entry.IsSelectable)
        {
            row.PointerEntered += (_, _) => { if (_selected != index) row.Background = HoverBrush; };
            row.PointerExited += (_, _) => { if (_selected != index) row.Background = Transparent; };
            row.Tapped += (_, _) => RowActivated?.Invoke(index);
        }

        return row;
    }

    private static void Highlight(Border row, bool selected)
    {
        row.Background = selected ? SelectedBrush : Transparent;
        ((Rectangle)row.Tag).Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigureNonActivating()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true; // WS_EX_TOPMOST set after creation is ignored; the presenter applies it
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        PopupInterop.MakeNonActivating(Hwnd);
        WindowStyling.TryRoundCorners(Hwnd);
    }

    private void TryEnableAcrylic()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            return;
        }

        var acrylic = new DesktopAcrylicController();
        acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        acrylic.SetSystemBackdropConfiguration(new SystemBackdropConfiguration { IsInputActive = true }); // never active: force it
        Closed += (_, _) => acrylic.Dispose();
    }
}
