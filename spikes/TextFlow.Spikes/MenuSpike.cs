using System.Threading.Channels;
using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Expansion;
using TextFlow.Core.Import;
using TextFlow.Core.Menus;
using TextFlow.Core.Operations;
using TextFlow.Core.Security;
using TextFlow.Infrastructure.Clipboard;
using TextFlow.Infrastructure.Feedback;
using TextFlow.Infrastructure.Hooks;
using TextFlow.Infrastructure.Input;
using TextFlow.Infrastructure.Targeting;

namespace TextFlow.Spikes;

/// <summary>Owns the STA UI thread of the <see cref="GroupMenuPopup"/>; callable from any thread.</summary>
internal sealed class MenuHost : IDisposable
{
    private readonly Thread _thread;
    private readonly GroupMenuPopup _popup;

    public MenuHost(Action<MenuStep> onFinished)
    {
        var ready = new TaskCompletionSource<GroupMenuPopup>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            var popup = new GroupMenuPopup(onFinished);
            _ = popup.Handle; // create the window on this thread so BeginInvoke works
            ready.SetResult(popup);
            Application.Run();
            popup.Dispose();
        })
        {
            IsBackground = true,
            Name = "TextFlow.MenuUi",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _popup = ready.Task.GetAwaiter().GetResult();
    }

    public void Open(GroupMenu menu, PixelRect anchor, PixelRect monitor, double scale) =>
        _popup.BeginInvoke(() => _popup.Open(menu, anchor, monitor, scale));

    public void Send(MenuInput input) => _popup.BeginInvoke(() => _popup.Apply(input));

    public void Dismiss() => _popup.BeginInvoke(_popup.Dismiss);

    public bool Contains(int x, int y) => _popup.VisibleBounds.Contains(x, y);

    public void Dispose()
    {
        _popup.BeginInvoke(Application.ExitThread);
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}

internal sealed record MenuFinished(MenuStep Step) : HookEvent;

/// <summary>
/// S7: group abbreviations from an aText backup open a non-activating menu at the caret; the hook
/// routes arrows/Enter/Esc/1-9 to it, so the target never loses focus. The chosen snippet replaces the abbreviation.
/// </summary>
internal static class MenuSpike
{
    private sealed record OpenMenu(ActiveTarget Target, TriggerMatch Match);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.WriteLine("Uso: menu <archivo.atext> [volumen 0-100]");
            return 1;
        }

        GroupMenuIndex index;
        try
        {
            using var file = File.OpenRead(args[0]);
            index = GroupMenuIndex.Build(ATextBackupReader.Read(file).Root);
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"No se pudo leer el backup: {ex.Message}");
            return 1;
        }

        var events = Channel.CreateUnbounded<HookEvent>(new UnboundedChannelOptions { SingleReader = true });
        using var host = new MenuHost(step => events.Writer.TryWrite(new MenuFinished(step)));
        using var hook = new KeyboardHook(new TriggerMatcher(index.Triggers, TriggerOptions.Default));
        using var clipboard = new ClipboardStrategy();
        var resolver = Services.CreateResolver();
        var policy = Services.CreatePolicy();
        var coordinator = new InsertionCoordinator(resolver, [clipboard, new SendInputStrategy()], new InsertionOptions());
        var volume = args.Length > 1 && int.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out var percent) ? percent / 100.0 : 1.0;
        var sound = new ExpansionSound(volume);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var pump = Task.Run(async () =>
        {
            await foreach (var evt in hook.Events.ReadAllAsync(cts.Token))
            {
                events.Writer.TryWrite(evt);
            }
        });

        await EvaluateForegroundAsync(hook, resolver, policy);
        Console.WriteLine($"{index.Menus.Count} menús cargados. Escribe una abreviatura de grupo (lc, od, cross…). Ctrl+C para salir.");

        OpenMenu? open = null;
        try
        {
            await foreach (var evt in events.Reader.ReadAllAsync(cts.Token))
            {
                switch (evt)
                {
                    case ForegroundChanged:
                        if (open is not null)
                        {
                            host.Dismiss();
                        }

                        await EvaluateForegroundAsync(hook, resolver, policy);
                        break;

                    case TriggerTyped typed when open is null:
                        open = await OpenAsync(typed, index, host, hook, resolver, policy);
                        break;

                    case MenuKeyPressed key when open is not null:
                        host.Send(key.Input);
                        break;

                    case MenuInterrupted click when open is not null:
                        if (click.ClickX is not { } x || click.ClickY is not { } y || !host.Contains(x, y))
                        {
                            host.Dismiss();
                        }

                        break;

                    case MenuFinished finished when open is not null:
                        hook.MenuMode = false;
                        await FinishAsync(open, finished.Step, coordinator, sound);
                        open = null;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C
        }

        hook.MenuMode = false;
        await pump.ContinueWith(_ => { }, TaskScheduler.Default);
        return 0;
    }

    private static async Task<OpenMenu?> OpenAsync(
        TriggerTyped typed, GroupMenuIndex index, MenuHost host, KeyboardHook hook, Win32TargetResolver resolver, SecurityPolicy policy)
    {
        var menu = index.Find(typed.Match.SnippetId);
        var target = await Task.Run(resolver.CaptureTarget);
        if (menu is null || target is null || target.WindowHandle != typed.ForegroundWindow
            || !policy.Evaluate(target, TextFlowFeature.Expansion).IsAllowed)
        {
            Console.WriteLine("  destino no válido; no se abre el menú.");
            return null;
        }

        var anchor = target.Control.CaretBounds ?? CursorAnchor();
        hook.MenuMode = true;
        host.Open(menu, anchor, target.Monitor.Bounds, target.Monitor.Scale);
        Console.WriteLine($"  menú {menu.Trigger} → {target.ProcessName} (ancla: {(target.Control.CaretBounds is null ? "ratón" : "caret")})");
        return new OpenMenu(target, typed.Match);
    }

    private static async Task FinishAsync(OpenMenu open, MenuStep step, InsertionCoordinator coordinator, ExpansionSound sound)
    {
        if (step.Chosen is not { } snippet)
        {
            Console.WriteLine("  menú cerrado sin elegir.");
            return;
        }

        var request = new InsertionRequest(open.Target, snippet.Content, open.Match.Backspaces);
        var result = await coordinator.InsertAsync(request, CancellationToken.None);
        if (result.Succeeded)
        {
            sound.Play();
        }

        Console.WriteLine($"  «{snippet.Label}» → {open.Target.ProcessName}: {result.Status} via {result.Strategy} " +
                          $"en {result.Elapsed.TotalMilliseconds:F0} ms {result.Detail}");
    }

    private static PixelRect CursorAnchor()
    {
        var p = Cursor.Position;
        return new PixelRect(p.X, p.Y, p.X, p.Y + 16);
    }

    private static async Task EvaluateForegroundAsync(KeyboardHook hook, Win32TargetResolver resolver, SecurityPolicy policy)
    {
        hook.CaptureEnabled = false;
        var target = await Task.Run(resolver.CaptureTarget);
        var allowed = target is not null && policy.Evaluate(target, TextFlowFeature.Expansion).IsAllowed;
        hook.CaptureEnabled = allowed;
        Console.WriteLine($"  foreground: {target?.ProcessName ?? "-"} → captura {(allowed ? "ON" : "OFF")}");
    }
}
