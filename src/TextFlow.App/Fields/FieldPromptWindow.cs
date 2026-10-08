using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TextFlow.App.Menu;
using TextFlow.Core.Templates;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace TextFlow.App.Fields;

/// <summary>
/// Fields of a template (H5.2, ADR-0002 rev. 2026-10-04). Unlike the group menu it takes the focus, stays on top while
/// the user goes to another window, and fills the current field with every text the user copies, then moves on.
/// Values live only in the text boxes and are cleared on close. UI thread only.
/// </summary>
internal sealed partial class FieldPromptWindow : Window
{
    private const double WidthDip = 400;
    private const double HeaderDip = 88;
    private const double RowDip = 64;
    private const double FooterDip = 64;
    private const double MessageDip = 170;

    private readonly StackPanel _rows = new() { Spacing = 4, Padding = new Thickness(16, 4, 16, 4) };
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _hint = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly Button _primary = new() { Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 96 };
    private readonly Button _secondary = new() { MinWidth = 96 };
    private readonly Grid _layout;
    private readonly List<TextBox> _boxes = [];
    private IReadOnlyList<TemplateField> _fields = [];
    private int _current;
    private bool _listening;
    private bool _messageMode;
    private bool _waitingMode;
    private (string Text, long At)? _lastFilled;

    /// <summary>One copy can raise ContentChanged twice (seen with PowerShell and browsers): fill one field per copy.</summary>
    private const long EchoWindowMs = 1000;

    private bool IsEcho(string text) =>
        _lastFilled is { } last && last.Text == text && Environment.TickCount64 - last.At < EchoWindowMs;

    public FieldPromptWindow()
    {
        var header = new StackPanel { Spacing = 2, Padding = new Thickness(16, 12, 16, 8) };
        header.Children.Add(_title);
        header.Children.Add(_hint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 12, 16, 16),
        };
        buttons.Children.Add(_secondary);
        buttons.Children.Add(_primary);
        _primary.Click += (_, _) => OnPrimary();
        _secondary.Click += (_, _) => Finish(null);

        _layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition(),
                new RowDefinition { Height = GridLength.Auto },
            },
        };
        var scroller = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroller, 1);
        Grid.SetRow(buttons, 2);
        _layout.Children.Add(header);
        _layout.Children.Add(scroller);
        _layout.Children.Add(buttons);
        _layout.PreviewKeyDown += OnPreviewKeyDown;
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
        if (File.Exists(App.IconFile))
        {
            AppWindow.SetIcon(App.IconFile); // shown in Alt+Tab and the taskbar while it waits
        }

        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true; // Alt+F4 means cancel, the window is reused
            Finish(null);
        };
    }

    /// <summary>Values when the user inserts, null when cancelled. Never raised for <see cref="ShowNotInserted"/>.</summary>
    public event Action<IReadOnlyDictionary<string, string>?>? Finished;

    public nint Hwnd { get; }

    public void ApplyTheme(AppTheme theme) => _layout.RequestedTheme = theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public void ShowFields(IReadOnlyList<TemplateField> fields, RectInt32 bounds)
    {
        _messageMode = false;
        _waitingMode = false;
        _fields = fields;
        _title.Text = fields.Count == 1 ? "Completa el dato" : $"Completa los {fields.Count} datos";
        _hint.Text = "Copia cada dato donde esté: se coloca solo en el campo marcado y pasa al siguiente.";
        _primary.Content = "Insertar";
        _secondary.Content = "Cancelar";
        _secondary.Visibility = Visibility.Visible;
        BuildRows();
        ShowAt(bounds);
        StartListening();
        MoveTo(0);
    }

    /// <summary>
    /// Values are in, the original field is not focused: say the text goes in once the user returns there. Shown
    /// without taking the focus, so the click that brings the user back is not lost on this window.
    /// </summary>
    public void ShowWaiting(RectInt32 bounds)
    {
        StopListening();
        ClearRows();
        _messageMode = true;
        _waitingMode = true;
        _title.Text = "Listo: vuelve a donde escribiste la abreviatura";
        _hint.Text = "Haz clic en ese campo (por ejemplo, la conversación) y el texto se insertará solo.";
        _primary.Content = "Cancelar";
        _secondary.Visibility = Visibility.Collapsed;
        ShowAt(bounds, activate: false);
    }

    /// <summary>The original field was not reachable: the text goes to the clipboard and the user is told.</summary>
    public void ShowNotInserted(string text, RectInt32 bounds)
    {
        StopListening(); // our own clipboard write must not fill anything
        ClearRows();
        _messageMode = true;
        _waitingMode = false;
        _title.Text = "No pude volver al sitio donde escribiste";
        _hint.Text = "El texto completo está en el portapapeles: haz clic donde quieras ponerlo y pulsa Ctrl+V.";
        _primary.Content = "Entendido";
        _secondary.Visibility = Visibility.Collapsed;

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush(); // keep it after TextFlow hides the window
        ShowAt(bounds);
        _primary.Focus(FocusState.Programmatic);
    }

    public void HidePrompt()
    {
        StopListening();
        ClearRows();
        AppWindow.Hide();
    }

    /// <summary>Window size in physical pixels for <paramref name="fieldCount"/> fields (0 = the not-inserted message).</summary>
    public static SizeInt32 SizeFor(int fieldCount, double scale)
    {
        var height = fieldCount == 0 ? MessageDip : HeaderDip + (Math.Min(fieldCount, 6) * RowDip) + FooterDip + 8;
        return new SizeInt32((int)Math.Round(WidthDip * scale), (int)Math.Round(height * scale));
    }

    private void BuildRows()
    {
        ClearRows();
        for (var i = 0; i < _fields.Count; i++)
        {
            var field = _fields[i];
            var box = new TextBox { Header = Label(field.Name), Text = field.DefaultValue ?? string.Empty };
            var index = i;
            box.GotFocus += (_, _) => Highlight(index);
            _boxes.Add(box);
            _rows.Children.Add(box);
        }
    }

    private void ClearRows()
    {
        _lastFilled = null;
        foreach (var box in _boxes)
        {
            box.Text = string.Empty;
        }

        _boxes.Clear();
        _rows.Children.Clear();
    }

    /// <summary>"monto" → "Monto"; underscores become spaces ("numero_pedido" → "Numero pedido").</summary>
    private static string Label(string name)
    {
        var text = name.Replace('_', ' ');
        return text.Length == 0 ? text : char.ToUpper(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..];
    }

    private void MoveTo(int index)
    {
        if (index >= _boxes.Count)
        {
            _primary.Focus(FocusState.Programmatic); // all filled: Enter inserts
            Highlight(-1);
            return;
        }

        _boxes[index].Focus(FocusState.Programmatic);
        Highlight(index);
    }

    private void Highlight(int index)
    {
        _current = index;
        for (var i = 0; i < _boxes.Count; i++)
        {
            _boxes[i].BorderBrush = i == index ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x3B, 0x82, 0xF6)) : null;
            _boxes[i].BorderThickness = i == index ? new Thickness(2) : new Thickness(1);
        }
    }

    private void StartListening()
    {
        if (!_listening)
        {
            Clipboard.ContentChanged += OnClipboardChanged;
            _listening = true;
        }
    }

    private void StopListening()
    {
        if (_listening)
        {
            Clipboard.ContentChanged -= OnClipboardChanged;
            _listening = false;
        }
    }

    private async void OnClipboardChanged(object? sender, object e)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text) || _current < 0 || _current >= _boxes.Count)
            {
                return;
            }

            var text = (await view.GetTextAsync()).Trim();
            if (text.Length == 0 || !_listening || _current < 0 || _current >= _boxes.Count || IsEcho(text))
            {
                return;
            }

            _lastFilled = (text, Environment.TickCount64);
            _boxes[_current].Text = text;
            var nextEmpty = _boxes.FindIndex(b => b.Text.Length == 0);
            if (nextEmpty < 0)
            {
                OnPrimary(); // the last value came from a copy: insert at once, no click or Enter needed
                return;
            }

            var laterEmpty = _current + 1 < _boxes.Count ? _boxes.FindIndex(_current + 1, b => b.Text.Length == 0) : -1;
            MoveTo(laterEmpty >= 0 ? laterEmpty : nextEmpty); // next empty field below, else the first one left empty
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Clipboard busy or a format we cannot read: the user can still paste by hand.
            App.Current.RecordFault(ex);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Finish(null);
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnPrimary();
        }
    }

    private void OnPrimary()
    {
        if (_waitingMode)
        {
            _waitingMode = false;
            _messageMode = false;
            Finish(null); // "Cancelar" on the waiting message
            return;
        }

        if (_messageMode)
        {
            HidePrompt();
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < _fields.Count; i++)
        {
            values[_fields[i].Name] = _boxes[i].Text;
        }

        Finish(values);
    }

    private void Finish(IReadOnlyDictionary<string, string>? values)
    {
        if (_waitingMode)
        {
            OnPrimary();
            return;
        }

        if (_messageMode)
        {
            HidePrompt();
            return;
        }

        HidePrompt();
        Finished?.Invoke(values);
    }

    private void ShowAt(RectInt32 bounds, bool activate = true)
    {
        AppWindow.MoveAndResize(bounds);
        if (AppWindow.Size.Width != bounds.Width || AppWindow.Size.Height != bounds.Height)
        {
            AppWindow.MoveAndResize(bounds); // a DPI change on the way rescaled it (see MenuPopup.ShowAt)
        }

        AppWindow.Show(activateWindow: activate);
        if (activate)
        {
            Activate();
        }

        PopupInterop.BringToTopmost(Hwnd);
    }
}
