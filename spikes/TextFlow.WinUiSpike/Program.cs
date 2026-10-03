using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Expansion;
using TextFlow.Core.Menus;
using TextFlow.Core.Operations;
using TextFlow.Infrastructure.Clipboard;
using TextFlow.Infrastructure.Feedback;
using TextFlow.Infrastructure.Hooks;
using TextFlow.Infrastructure.Input;
using TextFlow.Infrastructure.Targeting;

namespace TextFlow.WinUiSpike;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new SpikeApp();
        });
    }
}

/// <summary>
/// H0.1 harness: type "zz" in any app → WinUI popup at the caret. Arrows/Enter/Esc arrive through the hook
/// (MenuMode); clicks go to the popup. After every step it checks that the foreground window and the focused
/// control of the target did not change, and prints PASS/FAIL. Enter inserts the selected row (replacing "zz").
/// </summary>
internal sealed partial class SpikeApp : Application, Microsoft.UI.Xaml.Markup.IXamlMetadataProvider
{
    // Without App.xaml nothing generates the XAML type provider; XamlControlsResources needs it (else 0xC000027B).
    private readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider _xamlTypes = new();

    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(Type type) => _xamlTypes.GetXamlType(type);

    public Microsoft.UI.Xaml.Markup.IXamlType GetXamlType(string fullName) => _xamlTypes.GetXamlType(fullName);

    public Microsoft.UI.Xaml.Markup.XmlnsDefinition[] GetXmlnsDefinitions() => _xamlTypes.GetXmlnsDefinitions();

    private static readonly string[] Items =
    [
        "Local cerrado – No confirmado",
        "Local cerrado – No Near Pickup",
        "Orden demorada más de 20 minutos",
        "Solicito al rider dirigirse al pickup",
        "Gracias por tu paciencia",
    ];

    private PopupWindow? _popup;
    private KeyboardHook? _hook;
    private DispatcherQueue? _ui;
    private (nint Foreground, nint Focus) _before;
    private ActiveTarget? _target;
    private TriggerMatch? _match;
    private InsertionCoordinator? _coordinator;
    private ClipboardStrategy? _clipboard;
    private readonly ExpansionSound _sound = new();
    private int _passes;
    private int _fails;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UnhandledException += (_, e) => Console.WriteLine($"  ✗ excepción no controlada: {e.Exception}");
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        _ui = DispatcherQueue.GetForCurrentThread();

        var selfTest = Environment.GetCommandLineArgs().SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault();
        if (selfTest is not null)
        {
            _ = SelfTestAsync(selfTest);
            return;
        }

        _popup = new PopupWindow();
        _popup.ItemClicked += index => _ = ChooseAsync(index, "clic");
        _popup.Prime();

        var resolver = new Win32TargetResolver(new UiaFocusedControlInspector());
        _clipboard = new ClipboardStrategy();
        _coordinator = new InsertionCoordinator(resolver, [_clipboard, new SendInputStrategy()], new InsertionOptions());

        _hook = new KeyboardHook(new TriggerMatcher([new TriggerDefinition("demo", "zz", IgnoreCase: true)], TriggerOptions.Default))
        {
            CaptureEnabled = true,
        };

        _ = Task.Run(async () =>
        {
            await foreach (var evt in _hook.Events.ReadAllAsync())
            {
                _ui.TryEnqueue(() => _ = HandleAsync(evt, resolver));
            }
        });

        Console.WriteLine("H0.1 listo. Escribe zz en Notepad/Firefox/Word… (flechas, Enter, Esc, clic). Ctrl+C en esta consola para salir.");
    }

    /// <summary>Shows each popup variant at a fixed spot, logs its window state and screenshots it (no user needed).</summary>
    private async Task SelfTestAsync(string outputDirectory)
    {
        PopupOptions[] variants =
        [
            new(NonActivating: true, Acrylic: true),
            new(NonActivating: true, Acrylic: false),
            new(NonActivating: false, Acrylic: false),
        ];

        foreach (var options in variants)
        {
            var name = $"{(options.NonActivating ? "noact" : "normal")}-{(options.Acrylic ? "acrylic" : "solid")}";
            var popup = new PopupWindow(options);
            popup.Prime();
            await Task.Delay(300);
            popup.ShowAt(400, 300, $"selftest {name}", Items);
            await Task.Delay(1500);
            Native.GetWindowRect(popup.Hwnd, out var rect);
            var exStyle = Native.GetWindowLongPtr(popup.Hwnd, Native.GwlExStyle);
            Console.WriteLine($"{name}: visible={Native.IsWindowVisible(popup.Hwnd)} rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}) exStyle=0x{exStyle:X}");
            Native.CaptureScreen(rect, Path.Combine(outputDirectory, $"{name}.bmp"));
            popup.Close();
        }

        Exit();
    }

    private async Task HandleAsync(HookEvent evt, Win32TargetResolver resolver)
    {
        switch (evt)
        {
            case TriggerTyped typed when _match is null:
                _before = Native.FocusSnapshot();
                _target = resolver.CaptureTarget();
                _match = typed.Match;
                _hook!.MenuMode = true;
                var anchor = Native.CaretOrCursor() ?? (100, 100, 16);
                _popup!.ShowAt(anchor.X, anchor.Y + 4, $"zz  ›  demo WinUI 3", Items);
                await CheckAsync("mostrar popup");
                break;

            case MenuKeyPressed key when _match is not null:
                switch (key.Input.Kind)
                {
                    case MenuInputKind.Up:
                        _popup!.Select(_popup.SelectedIndex - 1);
                        await CheckAsync("flecha");
                        break;
                    case MenuInputKind.Down:
                        _popup!.Select(_popup.SelectedIndex + 1);
                        await CheckAsync("flecha");
                        break;
                    case MenuInputKind.Enter:
                        await ChooseAsync(_popup!.SelectedIndex, "Enter");
                        break;
                    case MenuInputKind.Number when key.Input.Value <= Items.Length:
                        await ChooseAsync(key.Input.Value - 1, $"número {key.Input.Value}");
                        break;
                    case MenuInputKind.Escape:
                        Close();
                        await CheckAsync("Esc");
                        break;
                }

                break;

            case MenuInterrupted interrupted when _match is not null && interrupted.ClickX is null:
                Close();
                await CheckAsync("otra tecla");
                break;

            case ForegroundChanged when _match is not null:
                Console.WriteLine("  ✗ FAIL: cambió la ventana en primer plano con el popup abierto");
                _fails++;
                Close();
                break;
        }
    }

    private async Task ChooseAsync(int index, string how)
    {
        if (_match is null || _target is null || index < 0 || index >= Items.Length)
        {
            return;
        }

        await CheckAsync($"elegir con {how}");
        var (target, match) = (_target, _match);
        Close();
        var result = await _coordinator!.InsertAsync(new InsertionRequest(target, Items[index], match.Backspaces), CancellationToken.None);
        if (result.Succeeded)
        {
            _sound.Play();
        }

        Console.WriteLine($"  inserción: {result.Status} via {result.Strategy} en {result.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"  total: {_passes} PASS, {_fails} FAIL");
    }

    private void Close()
    {
        _popup!.HidePopup();
        _hook!.MenuMode = false;
        _match = null;
    }

    private async Task CheckAsync(string step)
    {
        await Task.Delay(250); // let activation/focus messages settle
        var now = Native.FocusSnapshot();
        if (now == _before)
        {
            _passes++;
            Console.WriteLine($"  ✓ PASS {step}: foco intacto");
        }
        else
        {
            _fails++;
            Console.WriteLine($"  ✗ FAIL {step}: primer plano 0x{_before.Foreground:X}→0x{now.Foreground:X}, foco 0x{_before.Focus:X}→0x{now.Focus:X}");
        }
    }
}
