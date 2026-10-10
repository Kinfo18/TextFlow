using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TextFlow.App.Menu;
using TextFlow.Contracts.Targeting;
using Windows.Graphics;
using Windows.System;

namespace TextFlow.App.Palette;

/// <summary>One line of the palette: a snippet to insert or an action on TextFlow itself.</summary>
/// <param name="SnippetId">Set for snippets; null for actions.</param>
/// <param name="Run">Set for actions; null for snippets.</param>
internal sealed record PaletteItem(string Title, string Detail, string? SnippetId = null, Action? Run = null);

/// <summary>
/// Command palette (D11, spec section 22): a search box over the library and a few actions. Unlike the group menu it
/// takes the focus, because the user types into it; the engine puts the focus back on the original field before
/// inserting. Leaving it (Esc, a click elsewhere) closes it. UI thread only; one instance, reused.
/// </summary>
internal sealed partial class PaletteWindow : Window
{
    private const double WidthDip = 560;
    private const double HeightDip = 420;

    private readonly TextBox _search = new()
    {
        PlaceholderText = "Busca un comando por abreviatura, nombre o texto, o una acción",
        FontSize = 16,
        BorderThickness = new Thickness(0),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };

    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
    private readonly TextBlock _empty = new() { Opacity = 0.65, Margin = new Thickness(20, 8, 20, 8), TextWrapping = TextWrapping.Wrap };
    private readonly Grid _layout;
    private Func<string, IReadOnlyList<PaletteItem>> _items = _ => [];
    private bool _closing;

    public PaletteWindow()
    {
        AutomationProperties.SetName(_search, "Buscar en la paleta de comandos");
        AutomationProperties.SetName(_list, "Resultados");
        _search.TextChanged += (_, _) => Fill(_search.Text);
        _search.PreviewKeyDown += OnSearchKeyDown;
        _list.ItemClick += (_, e) => Choose(((FrameworkElement)e.ClickedItem).Tag as PaletteItem);

        var searchRow = new Grid { Padding = new Thickness(16, 12, 16, 8), ColumnSpacing = 10 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition());
        searchRow.Children.Add(new FontIcon { Glyph = "", FontSize = 16, Opacity = 0.7 });
        Grid.SetColumn(_search, 1);
        searchRow.Children.Add(_search);

        var hint = new TextBlock
        {
            Text = "↑↓ elegir · Enter insertar en el campo donde estabas · Esc cerrar",
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Thickness(20, 6, 20, 10),
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
        var results = new Grid();
        results.Children.Add(_list);
        results.Children.Add(_empty);
        Grid.SetRow(results, 1);
        Grid.SetRow(hint, 2);
        _layout.Children.Add(searchRow);
        _layout.Children.Add(results);
        _layout.Children.Add(hint);
        Content = _layout;

        SystemBackdrop = new DesktopAcrylicBackdrop();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true; // Alt+F4 closes the palette, the window is reused
            Close(null);
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                Close(null); // the user clicked elsewhere
            }
        };
    }

    /// <summary>The chosen item, or null when the palette closed without a choice. Raised after the window hid.</summary>
    public event Action<PaletteItem?>? Finished;

    public nint Hwnd { get; }

    public bool IsOpen { get; private set; }

    public void ApplyTheme(AppTheme theme) => _layout.RequestedTheme = theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <param name="items">Results for a query; called on every keystroke, so it must be quick.</param>
    /// <param name="monitor">Where the user is working: the palette opens in the upper part of that screen.</param>
    public void Open(Func<string, IReadOnlyList<PaletteItem>> items, MonitorInfo? monitor)
    {
        _items = items;
        _closing = false;
        IsOpen = true;
        _search.Text = string.Empty;
        Fill(string.Empty);
        ShowAt(PlaceOn(monitor));
        _search.Focus(FocusState.Programmatic);
    }

    private static RectInt32 PlaceOn(MonitorInfo? monitor)
    {
        var area = monitor?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
        var scale = monitor?.Scale ?? 1.0;
        var width = (int)Math.Round(WidthDip * scale);
        var height = (int)Math.Round(HeightDip * scale);
        return new RectInt32(area.Left + ((area.Width - width) / 2), area.Top + (area.Height / 5), width, height);
    }

    private void Fill(string query)
    {
        var items = _items(query);
        _list.Items.Clear();
        foreach (var item in items)
        {
            _list.Items.Add(Row(item));
        }

        _list.SelectedIndex = items.Count > 0 ? 0 : -1;
        _empty.Text = query.Trim().Length == 0 ? "Escribe para buscar en tu biblioteca." : "Nada coincide con la búsqueda.";
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ListViewItem Row(PaletteItem item)
    {
        var panel = new Grid { ColumnSpacing = 12, Padding = new Thickness(6, 4, 6, 4) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.Children.Add(new FontIcon
        {
            Glyph = item.SnippetId is null ? "" : "",
            FontSize = 14,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = item.Detail, FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        panel.Children.Add(text);
        var row = new ListViewItem { Content = panel, Tag = item };
        AutomationProperties.SetName(row, $"{item.Title}: {item.Detail}");
        return row;
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                Move(+1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                Move(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                Choose((_list.SelectedItem as FrameworkElement)?.Tag as PaletteItem);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Close(null);
                e.Handled = true;
                break;
        }
    }

    private void Move(int step)
    {
        if (_list.Items.Count == 0)
        {
            return;
        }

        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + step, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void Choose(PaletteItem? item)
    {
        if (item is not null)
        {
            Close(item);
        }
    }

    /// <summary>Hides first, then reports: the original window must get the focus back before anything is inserted.</summary>
    private void Close(PaletteItem? choice)
    {
        if (_closing || !IsOpen)
        {
            return;
        }

        _closing = true;
        IsOpen = false;
        AppWindow.Hide();
        _search.Text = string.Empty; // what was typed here never outlives the palette
        _list.Items.Clear();
        Finished?.Invoke(choice);
    }

    private void ShowAt(RectInt32 bounds)
    {
        AppWindow.MoveAndResize(bounds);
        if (AppWindow.Size.Width != bounds.Width || AppWindow.Size.Height != bounds.Height)
        {
            AppWindow.MoveAndResize(bounds); // a DPI change on the way rescaled it (see MenuPopup.ShowAt)
        }

        AppWindow.Show(activateWindow: true);
        Activate();
        PopupInterop.BringToTopmost(Hwnd);
        PopupInterop.ForceForeground(Hwnd); // opened from a global shortcut while another app is in front
    }
}
