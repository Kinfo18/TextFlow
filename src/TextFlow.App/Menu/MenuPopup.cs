using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TextFlow.Core.Menus;
using TextFlow.Infrastructure.Windows;
using Windows.Graphics;
using WinRT;

namespace TextFlow.App.Menu;

/// <summary>
/// Group menu window (ADR-0008, final look H4.1): never activates, so the target keeps focus and caret. Breadcrumb
/// header with the app gradient, numbered rows (1-9) as pills, info entries in italics, › for subgroups, a key hint
/// footer, acrylic and rounded corners, the app's light/dark theme and a short entrance (skipped when Windows
/// animations are off). Keyboard input arrives from the hook through <see cref="WinUiMenuPresenter"/>; UI thread only.
/// </summary>
internal sealed partial class MenuPopup : Window
{
    public const int MaxVisibleRows = 12;
    private const double HeaderDip = 38;
    private const double FooterDip = 26;
    private const double RowDip = 36;
    private const double PaddingDip = 8;
    private const double MinWidthDip = 300;
    private const double MaxWidthDip = 560;
    private const double CharWidthDip = 7.2; // Segoe UI 14 px average, good enough to size the window
    private const double PillDip = 20;
    private static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(120);
    private const float EntranceOffset = 6;

    private static readonly string Chevron = ((char)0xE76C).ToString(); // ChevronRight (Segoe Fluent Icons)
    private static readonly Windows.UI.Color Blue = ColorHelper.FromArgb(0xFF, 0x3B, 0x82, 0xF6);
    private static readonly Windows.UI.Color Violet = ColorHelper.FromArgb(0xFF, 0x8B, 0x5C, 0xF6);
    private static readonly SolidColorBrush AccentBrush = new(Blue);
    private static readonly SolidColorBrush SelectedBrush = new(ColorHelper.FromArgb(0x33, 0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush HoverBrush = new(ColorHelper.FromArgb(0x16, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush PillBrush = new(ColorHelper.FromArgb(0x24, 0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush Transparent = new(Colors.Transparent);
    private static readonly SolidColorBrush White = new(Colors.White);

    private readonly Grid _layout;
    private readonly Border _header;
    private readonly TextBlock _breadcrumb;
    private readonly StackPanel _rows;
    private readonly ScrollViewer _scroller;
    private readonly List<Border> _rowViews = [];
    private readonly SystemBackdropConfiguration _backdrop = new() { IsInputActive = true }; // never active: force it
    private int _selected = -1;

    public MenuPopup()
    {
        _breadcrumb = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var brandDot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Gradient(Blue, Violet, 0xFF, 0xFF),
        };

        var headerContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 0, 14, 0) };
        headerContent.Children.Add(brandDot);
        headerContent.Children.Add(_breadcrumb);
        _header = new Border { Height = HeaderDip, Child = headerContent };

        _rows = new StackPanel { Padding = new Thickness(4) };
        _scroller = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var footer = new Border
        {
            Height = FooterDip,
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(0x1A, 0x80, 0x80, 0x80)),
            Child = new TextBlock
            {
                Text = "↑↓ elegir  ·  Enter insertar  ·  ← volver  ·  Esc cerrar",
                FontSize = 11,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 14, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };

        _layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition(),
                new RowDefinition { Height = GridLength.Auto },
            },
        };
        Grid.SetRow(_scroller, 1);
        Grid.SetRow(footer, 2);
        _layout.Children.Add(_header);
        _layout.Children.Add(_scroller);
        _layout.Children.Add(footer);
        Content = _layout;

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ConfigureNonActivating();
        TryEnableAcrylic();
        ApplyTheme(AppTheme.System);
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

    /// <summary>Follows the theme chosen in Configuración (text, acrylic tint and header gradient).</summary>
    public void ApplyTheme(AppTheme theme)
    {
        _layout.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _backdrop.Theme = theme switch
        {
            AppTheme.Light => SystemBackdropTheme.Light,
            AppTheme.Dark => SystemBackdropTheme.Dark,
            _ => SystemBackdropTheme.Default,
        };

        var dark = _layout.ActualTheme == ElementTheme.Dark
            || (theme == AppTheme.System && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        _header.Background = dark ? Gradient(Blue, Violet, 0x55, 0x26) : Gradient(Blue, Violet, 0x38, 0x14);
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
        var width = Math.Clamp((longest * CharWidthDip) + 100, Math.Max(MinWidthDip, (_breadcrumb.Text.Length * CharWidthDip) + 50), MaxWidthDip);
        var height = HeaderDip + (Math.Clamp(level.Entries.Count, 1, MaxVisibleRows) * RowDip) + PaddingDip + FooterDip + 2;
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
        var wasVisible = AppWindow.IsVisible;
        AppWindow.MoveAndResize(bounds);
        if (AppWindow.Position.X != bounds.X || AppWindow.Position.Y != bounds.Y
            || AppWindow.Size.Width != bounds.Width || AppWindow.Size.Height != bounds.Height)
        {
            // Crossing to a monitor with another DPI: Windows rescaled the size we had already computed for that
            // monitor (125 % twice = empty space under the rows). Now the window has the new DPI: apply it again.
            AppWindow.MoveAndResize(bounds);
        }

        AppWindow.Show(activateWindow: false);
        PopupInterop.BringToTopmost(Hwnd);
        if (!wasVisible)
        {
            PlayEntrance(); // entering a subgroup re-presents an open menu: no second entrance
        }
    }

    public void HidePopup() => AppWindow.Hide();

    /// <summary>Fade in while rising a few pixels; respects "Animation effects" off in Windows (reduced motion).</summary>
    private void PlayEntrance()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_layout);
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            visual.Opacity = 1;
            visual.Offset = default;
            return;
        }

        var compositor = visual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = EntranceDuration;

        var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new System.Numerics.Vector3(0, EntranceOffset, 0));
        rise.InsertKeyFrame(1, System.Numerics.Vector3.Zero, compositor.CreateCubicBezierEasingFunction(new(0.16f, 1f), new(0.3f, 1f)));
        rise.Duration = EntranceDuration;

        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Offset", rise);
    }

    private sealed record RowParts(Rectangle AccentBar, Border? Pill, TextBlock? Number);

    private Border CreateRow(MenuLevel level, int index)
    {
        var entry = level.Entries[index];
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(34) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(22) },
            },
        };

        var accentBar = new Rectangle
        {
            Width = 3,
            Height = 16,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = AccentBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };
        grid.Children.Add(accentBar);

        Border? pill = null;
        TextBlock? numberText = null;
        if (level.NumberOf(entry) is { } number)
        {
            numberText = new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalTextAlignment = TextAlignment.Center,
                TextLineBounds = TextLineBounds.Tight, // box = the digit itself, so it sits in the middle of the circle
            };
            pill = new Border
            {
                Width = PillDip,
                Height = PillDip,
                CornerRadius = new CornerRadius(PillDip / 2),
                Background = PillBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = numberText,
            };
            grid.Children.Add(pill);
        }
        else if (!entry.IsSelectable)
        {
            grid.Children.Add(new TextBlock
            {
                Text = "ⓘ",
                Opacity = 0.55,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

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
            var chevron = new FontIcon { Glyph = Chevron, FontSize = 11, Opacity = 0.6 };
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
        }

        var row = new Border
        {
            Height = RowDip,
            CornerRadius = new CornerRadius(6),
            Child = grid,
            Background = Transparent,
            Tag = new RowParts(accentBar, pill, numberText),
        };
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
        var parts = (RowParts)row.Tag;
        row.Background = selected ? SelectedBrush : Transparent;
        parts.AccentBar.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (parts.Pill is not null && parts.Number is not null)
        {
            parts.Pill.Background = selected ? AccentBrush : PillBrush;
            parts.Number.Foreground = selected ? White : AccentBrush;
        }
    }

    private static LinearGradientBrush Gradient(Windows.UI.Color from, Windows.UI.Color to, byte fromAlpha, byte toAlpha) => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 1),
        GradientStops =
        {
            new GradientStop { Color = ColorHelper.FromArgb(fromAlpha, from.R, from.G, from.B), Offset = 0 },
            new GradientStop { Color = ColorHelper.FromArgb(toAlpha, to.R, to.G, to.B), Offset = 1 },
        },
    };

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
        acrylic.SetSystemBackdropConfiguration(_backdrop);
        Closed += (_, _) => acrylic.Dispose();
    }
}
