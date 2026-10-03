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

    /// <summary>Hides without reporting a <see cref="MenuStep"/> (the trigger turned out to be a longer one).</summary>
    public void Cancel() => _popup.BeginInvoke(_popup.Cancel);

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
    /// <summary>How long an ambiguous trigger ("dir" while "dir1" exists) waits for more typing.</summary>
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromMilliseconds(600);

    private sealed record OpenMenu(ActiveTarget Target, TriggerMatch Match);

    private sealed record MenuServices(
        LibraryIndex Index, MenuHost Host, KeyboardHook Hook, Win32TargetResolver Resolver, SecurityPolicy Policy,
        InsertionCoordinator Coordinator, SendInputStrategy SendInput, ExpansionSound Sound);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.WriteLine("Uso: menu <archivo.atext> [volumen 0-100]");
            return 1;
        }

        LibraryIndex index;
        try
        {
            using var file = File.OpenRead(args[0]);
            index = LibraryIndex.Build(ATextBackupReader.Read(file).Root);
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
        var sendInput = new SendInputStrategy();
        var coordinator = new InsertionCoordinator(resolver, [clipboard, sendInput], new InsertionOptions());
        var volume = args.Length > 1 && int.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out var percent) ? percent / 100.0 : 1.0;
        var sound = new ExpansionSound(volume);
        var services = new MenuServices(index, host, hook, resolver, policy, coordinator, sendInput, sound);

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
        Console.WriteLine($"{index.Menus.Count} menús y {index.Triggers.Count - index.Menus.Count} comandos directos cargados. " +
                          "Escribe una abreviatura (lc, od, cc, s1, orca3…). Ctrl+C para salir.");

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

                    case TriggerPending pending when index.FindMenu(pending.Match.SnippetId) is not null:
                        // Menus open at once; a key that continues a longer trigger ("cp" + '1') still fires it.
                        open = await HandleTriggerAsync(new TriggerTyped(pending.Match, pending.ForegroundWindow), services);
                        break;

                    case TriggerPending pending:
                        _ = Task.Delay(PendingTimeout, cts.Token).ContinueWith(
                            _ => hook.FlushPendingAsync(pending.Version), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                        break;

                    case TriggerTyped typed:
                        if (open is not null)
                        {
                            host.Cancel(); // "cp" menu was open and the user completed "cp1"
                            hook.MenuMode = false;
                            open = null;
                        }

                        open = await HandleTriggerAsync(typed, services);
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
                        await FinishAsync(open, finished.Step, services);
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

    private static async Task<OpenMenu?> HandleTriggerAsync(TriggerTyped typed, MenuServices services)
    {
        var match = typed.Match;
        var target = await Task.Run(services.Resolver.CaptureTarget);
        if (target is null || target.WindowHandle != typed.ForegroundWindow
            || !services.Policy.Evaluate(target, TextFlowFeature.Expansion).IsAllowed)
        {
            Console.WriteLine("  destino no válido; se cancela.");
            await ReEmitAsync(target, match.Delimiter, services);
            return null;
        }

        if (services.Index.FindSnippet(match.SnippetId) is { } snippet)
        {
            // A pending trigger broken by another key carries that key as Delimiter: type it after the expansion.
            var trailing = match.Delimiter is { } d and not ('\r' or '\t') ? d.ToString() : string.Empty;
            await InsertAsync(target, snippet, snippet.Content + trailing, match.Backspaces, services);
            return null;
        }

        if (services.Index.FindMenu(match.SnippetId) is not { } menu)
        {
            return null;
        }

        if (match.Delimiter is not null)
        {
            // "dir" + another key: the user kept typing, so no menu (same as a menu dismissed by that key).
            await ReEmitAsync(target, match.Delimiter, services);
            return null;
        }

        var anchor = target.Control.CaretBounds ?? CursorAnchor();
        services.Hook.MenuMode = true;
        services.Host.Open(menu, anchor, target.Monitor.Bounds, target.Monitor.Scale);
        Console.WriteLine($"  menú {menu.Trigger} → {target.ProcessName} (ancla: {(target.Control.CaretBounds is null ? "ratón" : "caret")})");
        return new OpenMenu(target, match);
    }

    private static async Task FinishAsync(OpenMenu open, MenuStep step, MenuServices services)
    {
        if (step.Chosen is not { } snippet)
        {
            Console.WriteLine("  menú cerrado sin elegir.");
            return;
        }

        await InsertAsync(open.Target, snippet, snippet.Content, open.Match.Backspaces, services);
    }

    private static async Task InsertAsync(ActiveTarget target, MenuSnippetEntry snippet, string text, int backspaces, MenuServices services)
    {
        var result = await services.Coordinator.InsertAsync(new InsertionRequest(target, text, backspaces), CancellationToken.None);
        var sound = result.Succeeded ? (services.Sound.Play() ? "sonido ok" : "sonido FALLÓ") : "sin sonido";
        Console.WriteLine($"  «{snippet.Label}» → {target.ProcessName}: {result.Status} via {result.Strategy} " +
                          $"en {result.Elapsed.TotalMilliseconds:F0} ms, {sound} {result.Detail}");
    }

    private static async Task ReEmitAsync(ActiveTarget? target, char? swallowed, MenuServices services)
    {
        if (target is not null && swallowed is { } key)
        {
            var text = key == '\r' ? "\n" : key.ToString(); // TypeText turns \n into a real Enter key
            await services.SendInput.InsertAsync(new InsertionRequest(target, text), CancellationToken.None);
        }
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
