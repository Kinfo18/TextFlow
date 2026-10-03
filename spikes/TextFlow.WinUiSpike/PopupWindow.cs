using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TextFlow.Infrastructure.Windows;
using Windows.Graphics;
using WinRT;

namespace TextFlow.WinUiSpike;

/// <summary>
/// H0.1 (risk R1): a WinUI 3 window that never activates, so the target keeps focus and caret.
/// Context-menu presenter (no caption), WS_EX_NOACTIVATE | TOOLWINDOW | TOPMOST, WM_MOUSEACTIVATE → MA_NOACTIVATE,
/// AppWindow.Show(activateWindow: false), DWM rounded corners, acrylic forced "active" (an inactive window
/// would otherwise fall back to a solid color).
/// </summary>
/// <param name="NonActivating">Apply WS_EX_NOACTIVATE, context-menu presenter and MA_NOACTIVATE (self-test bisection).</param>
/// <param name="Acrylic">Use the forced-active DesktopAcrylicController backdrop.</param>
internal sealed record PopupOptions(bool NonActivating = true, bool Acrylic = true);

internal sealed partial class PopupWindow : Window
{
    private const int WidthDip = 340;
    private const int RowDip = 40; // ListViewItem MinHeight (40 DIP in WinUI 3 default style)
    private const int HeaderDip = 34;

    private readonly ListView _list;
    private readonly TextBlock _header;
    private readonly nint _hwnd;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    public PopupWindow(PopupOptions? options = null)
    {
        options ??= new PopupOptions();
        _header = new TextBlock
        {
            Margin = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = 0.75,
        };

        var headerBar = new Border
        {
            Height = HeaderDip,
            Child = _header,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = ColorHelper.FromArgb(0x40, 0x3B, 0x82, 0xF6), Offset = 0 },
                    new GradientStop { Color = ColorHelper.FromArgb(0x18, 0x8B, 0x5C, 0xF6), Offset = 1 },
                },
            },
        };

        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            Padding = new Thickness(4),
        };
        _list.ItemClick += (_, e) => ItemClicked?.Invoke(_list.Items.IndexOf(e.ClickedItem));

        var layout = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        Grid.SetRow(_list, 1);
        layout.Children.Add(headerBar);
        layout.Children.Add(_list);
        Content = layout;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (options.NonActivating)
        {
            ConfigureNonActivating();
        }

        if (options.Acrylic)
        {
            TryEnableAcrylic();
        }
        else
        {
            layout.Background = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xFB, 0xFB, 0xFD));
        }
    }

    /// <summary>Index of the clicked row; raised on the UI thread.</summary>
    public event Action<int>? ItemClicked;

    public int SelectedIndex => _list.SelectedIndex;

    public int Count => _list.Items.Count;

    public void ShowAt(int x, int y, string title, IReadOnlyList<string> items)
    {
        _header.Text = title;
        _list.Items.Clear();
        foreach (var item in items)
        {
            _list.Items.Add(item);
        }

        _list.SelectedIndex = 0;

        var scale = Native.DpiScale(_hwnd);
        var width = (int)(WidthDip * scale);
        var height = (int)((HeaderDip + 16 + (items.Count * RowDip)) * scale);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        AppWindow.Show(activateWindow: false);
    }

    public void Select(int index)
    {
        if (Count > 0)
        {
            _list.SelectedIndex = (index + Count) % Count;
            _list.ScrollIntoView(_list.SelectedItem);
        }
    }

    public void HidePopup() => AppWindow.Hide();

    public nint Hwnd => _hwnd;

    /// <summary>
    /// A WinUI window renders nothing until it has been activated once. Do that off-screen at startup (the only
    /// moment TextFlow takes focus), then hide; later shows use Show(activateWindow: false).
    /// </summary>
    public void Prime()
    {
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 10, 10));
        Activate();
        AppWindow.Hide();
    }

    private unsafe void ConfigureNonActivating()
    {
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true; // WS_EX_TOPMOST set after creation is ignored; the presenter applies it
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        var style = Native.GetWindowLongPtr(_hwnd, Native.GwlExStyle);
        Native.SetWindowLongPtr(_hwnd, Native.GwlExStyle,
            (nint)((long)style | Native.WsExNoActivate | Native.WsExToolWindow));

        Native.SetWindowSubclass(_hwnd, &NoActivateProc, 1, 0);
        WindowStyling.TryRoundCorners(_hwnd);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static nint NoActivateProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data) =>
        message == Native.WmMouseActivate ? Native.MaNoActivate : Native.DefSubclassProc(hwnd, message, wParam, lParam);

    private void TryEnableAcrylic()
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            return;
        }

        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true }; // never "active": force it
        _acrylic = new DesktopAcrylicController();
        _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
        Closed += (_, _) => _acrylic?.Dispose();
    }
}
