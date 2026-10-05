using System.Globalization;
using System.Text;
using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Expansion;
using TextFlow.Core.Import;
using TextFlow.Core.Operations;
using TextFlow.Core.Security;
using TextFlow.Core.Templates;
using TextFlow.Infrastructure.Clipboard;
using TextFlow.Infrastructure.Feedback;
using TextFlow.Core.Input;
using TextFlow.Infrastructure.Hooks;
using TextFlow.Infrastructure.Input;
using TextFlow.Infrastructure.Targeting;
using TextFlow.Core.Library;

Console.OutputEncoding = Encoding.UTF8;
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); // physical pixels for caret coordinates and the menu popup

var command = args.FirstOrDefault() ?? "help";
return command switch
{
    "target" => await TargetSpike.RunAsync(args.Skip(1).ToArray()),
    "insert" => await InsertSpike.RunAsync(args.Skip(1).ToArray()),
    "expand" => await ExpandSpike.RunAsync(),
    "import" => ImportSpike.Run(args.Skip(1).ToArray()),
    "menu" => await TextFlow.Spikes.MenuSpike.RunAsync(args.Skip(1).ToArray()),
    "focus" => TextFlow.Spikes.FocusProbe.Run(args.Skip(1).ToArray()),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        TextFlow Fase 0 spikes

          target [segundos]              Captura el destino cada segundo (S1)
          insert <auto|clipboard|sendinput> [texto]
                                         Cuenta atrás 3 s, captura destino e inserta (S2)
          expand                         Hook global + snippets demo instantáneos: ;firma ;fecha ;cur ;mail CC (S3)
          import <archivo.atext>         Lee un backup de aText y muestra resumen e incidencias (S6)
          menu <archivo.atext> [vol]     Abreviaturas de grupo abren menú en el caret; vol 0-100 del chime (S7)
        """);
    return 0;
}

internal static class Services
{
    public static Win32TargetResolver CreateResolver() => new(new UiaFocusedControlInspector());

    public static SecurityPolicy CreatePolicy() => new(BuiltinExclusions.All);

    public static string Describe(ActiveTarget t) =>
        $"{t.ProcessName,-22} pid={t.ProcessId,-6} hwnd=0x{t.WindowHandle:X} focus=0x{t.FocusHandle:X} class={t.WindowClass}\n" +
        $"    control={t.Control.ControlType}/{t.Control.FrameworkId} password={t.Control.IsPassword} readonly={t.Control.IsReadOnly} " +
        $"caret={t.Control.CaretBounds?.ToString() ?? "-"}\n" +
        $"    monitor={t.Monitor.DeviceName} dpi={t.Monitor.DpiX} ({t.Monitor.Scale:P0}) bounds={t.Monitor.Bounds} elevated={t.IsElevated} " +
        $"title=<{t.WindowTitle.Length} chars>";

    public static async Task CountdownAsync(int seconds)
    {
        for (var i = seconds; i > 0; i--)
        {
            Console.Write($"\r  Enfoca el destino... {i} ");
            await Task.Delay(1000);
        }

        Console.WriteLine();
    }
}

internal static class TargetSpike
{
    public static async Task<int> RunAsync(string[] args)
    {
        var seconds = args.Length > 0 && int.TryParse(args[0], CultureInfo.InvariantCulture, out var s) ? s : 15;
        var resolver = Services.CreateResolver();
        var policy = Services.CreatePolicy();
        ActiveTarget? first = null;

        for (var i = 0; i < seconds; i++)
        {
            var started = DateTime.UtcNow;
            var target = resolver.CaptureTarget();
            var ms = (DateTime.UtcNow - started).TotalMilliseconds;
            if (target is null)
            {
                Console.WriteLine("  (sin destino)");
            }
            else
            {
                first ??= target;
                var decision = policy.Evaluate(target, TextFlowFeature.Expansion);
                Console.WriteLine($"[{ms,5:F0} ms] {Services.Describe(target)}");
                Console.WriteLine($"    policy={(decision.IsAllowed ? "ALLOW" : $"DENY {decision.Reason} {decision.RuleId}")} " +
                                  $"firstTargetValidation={resolver.ValidateTarget(first).Status}");
            }

            await Task.Delay(1000);
        }

        return 0;
    }
}

internal static class InsertSpike
{
    private const string DefaultText = "Hola, ¿qué tal? Ñandú — 42 € 👍\r\nSegunda línea con acentos: áéíóú";

    public static async Task<int> RunAsync(string[] args)
    {
        InsertionStrategyKind? preferred = args.FirstOrDefault() switch
        {
            "clipboard" => InsertionStrategyKind.Clipboard,
            "sendinput" => InsertionStrategyKind.SendInput,
            _ => null,
        };
        var text = args.Length > 1 ? string.Join(' ', args.Skip(1)) : DefaultText;

        using var clipboard = new ClipboardStrategy();
        var resolver = Services.CreateResolver();
        var coordinator = new InsertionCoordinator(resolver, [clipboard, new SendInputStrategy()], new InsertionOptions());

        await Services.CountdownAsync(3);
        var target = resolver.CaptureTarget();
        if (target is null)
        {
            Console.WriteLine("Sin destino.");
            return 1;
        }

        Console.WriteLine(Services.Describe(target));
        var decision = Services.CreatePolicy().Evaluate(target, TextFlowFeature.Expansion);
        if (!decision.IsAllowed)
        {
            Console.WriteLine($"SAFE MODE: bloqueado por política ({decision.Reason} {decision.RuleId}).");
            return 2;
        }

        var result = await coordinator.InsertAsync(new InsertionRequest(target, text, PreferredStrategy: preferred), CancellationToken.None);
        Console.WriteLine($"Resultado: {result.Status} via {result.Strategy} en {result.Elapsed.TotalMilliseconds:F0} ms {result.Detail}");
        return result.Succeeded ? 0 : 3;
    }
}

internal static class ExpandSpike
{
    private static readonly Dictionary<string, (string Trigger, string Template)> Snippets = new()
    {
        ["firma"] = (";firma", "Saludos cordiales,\nDavid\n{{date:dddd d 'de' MMMM 'de' yyyy}}"),
        ["fecha"] = (";fecha", "{{date}}"),
        ["cur"] = (";cur", "Estimado/a {{cursor}}:\n\nGracias por su mensaje."),
        ["mail"] = (";mail", "nombre@ejemplo.com"),
        ["cc"] = ("CC", "Mensaje de prueba del comando CC."),
    };

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-ES");
    private static readonly ExpansionSound Sound = new();

    public static async Task<int> RunAsync()
    {
        var triggers = Snippets.Select(s => new TriggerDefinition(s.Key, s.Value.Trigger));
        using var hook = new KeyboardHook(new TriggerMatcher(triggers, TriggerOptions.Default));
        using var clipboard = new ClipboardStrategy();
        var resolver = Services.CreateResolver();
        var policy = Services.CreatePolicy();
        var sendInput = new SendInputStrategy();
        var coordinator = new InsertionCoordinator(resolver, [clipboard, sendInput], new InsertionOptions());

        await EvaluateForegroundAsync(hook, resolver, policy);
        Console.WriteLine("Hook activo. Escribe ;firma ;fecha ;cur ;mail CC en cualquier app (expanden al instante). Ctrl+C para salir.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await foreach (var evt in hook.Events.ReadAllAsync(cts.Token))
            {
                switch (evt)
                {
                    case ForegroundChanged:
                        await EvaluateForegroundAsync(hook, resolver, policy);
                        break;
                    case TriggerTyped typed:
                        await ExpandAsync(typed, resolver, policy, coordinator, sendInput);
                        Console.WriteLine($"    hook stats: {hook.Stats}");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C
        }

        return 0;
    }

    private static async Task EvaluateForegroundAsync(KeyboardHook hook, Win32TargetResolver resolver, SecurityPolicy policy)
    {
        hook.CaptureEnabled = false;
        var target = await Task.Run(resolver.CaptureTarget);
        var allowed = target is not null && policy.Evaluate(target, TextFlowFeature.Expansion).IsAllowed;
        hook.CaptureEnabled = allowed;
        Console.WriteLine($"  foreground: {target?.ProcessName ?? "-"} → captura {(allowed ? "ON" : "OFF")}");
    }

    private static async Task ExpandAsync(
        TriggerTyped typed, Win32TargetResolver resolver, SecurityPolicy policy, InsertionCoordinator coordinator, SendInputStrategy sendInput)
    {
        var match = typed.Match;
        var target = await Task.Run(resolver.CaptureTarget);
        if (target is null || target.WindowHandle != typed.ForegroundWindow || !policy.Evaluate(target, TextFlowFeature.Expansion).IsAllowed)
        {
            Console.WriteLine($"  {match.SnippetId}: destino no válido; se cancela la expansión.");
            if (target is not null && match.Delimiter is { } swallowed)
            {
                await sendInput.InsertAsync(new InsertionRequest(target, swallowed.ToString()), CancellationToken.None);
            }

            return;
        }

        var rendered = TemplateRenderer.Render(
            TemplateParser.Parse(Snippets[match.SnippetId].Template),
            new Dictionary<string, string>(),
            new SystemVariables(),
            Culture);

        // Space/punctuation are re-emitted after the expansion; Enter/Tab are not (they would send/move focus).
        // Immediate triggers have no delimiter to re-emit.
        var keepDelimiter = match.Delimiter is { } delimiter && delimiter is not ('\r' or '\t');
        var text = keepDelimiter ? rendered.Text + match.Delimiter : rendered.Text;
        var caret = rendered.CaretOffsetFromEnd > 0 && keepDelimiter ? rendered.CaretOffsetFromEnd + 1 : rendered.CaretOffsetFromEnd;

        var result = await coordinator.InsertAsync(
            new InsertionRequest(target, text, match.Backspaces, CaretOffsetFromEnd: caret), CancellationToken.None);
        if (result.Succeeded)
        {
            Sound.Play();
        }

        Console.WriteLine($"  {match.SnippetId} → {target.ProcessName}: {result.Status} via {result.Strategy} " +
                          $"en {result.Elapsed.TotalMilliseconds:F0} ms {result.Detail}");
    }

    private sealed class SystemVariables : IVariableSource
    {
        public DateTimeOffset Now => DateTimeOffset.Now;

        public string? GetClipboardText() => null; // S2 follow-up: read via ClipboardStore on its thread

        public string? GetSelectionText() => null;
    }
}

internal static class ImportSpike
{
    /// <summary>Prints structure and issues only; snippet content never reaches the console.</summary>
    public static int Run(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.WriteLine("Uso: import <archivo.atext>");
            return 1;
        }

        ATextImport import;
        try
        {
            using var file = File.OpenRead(args[0]);
            import = ATextBackupReader.Read(file);
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"No se pudo leer el backup: {ex.Message}");
            return 1;
        }

        var groups = Flatten(import.Root).ToArray();
        var withAbbreviation = groups.Where(g => g.Abbreviation is not null).ToArray();
        Console.WriteLine($"Grupos: {groups.Length} (con abreviatura: {withAbbreviation.Length})");
        Console.WriteLine($"Snippets: {groups.Sum(g => g.Snippets.Count)} (informativos sin texto: {groups.Sum(g => g.Snippets.Count(s => s.IsInfoOnly))})");
        Console.WriteLine();
        Print(import.Root, 0);
        Console.WriteLine();
        Console.WriteLine($"Incidencias: {import.Issues.Count}");
        foreach (var issue in import.Issues.GroupBy(i => i.Code))
        {
            Console.WriteLine($"  {issue.Key}: {issue.Count()}");
            foreach (var item in issue.Where(i => i.Code is ImportIssueCode.DuplicateAbbreviation or ImportIssueCode.UnknownField))
            {
                Console.WriteLine($"    - {item.Detail}");
            }
        }

        return 0;
    }

    private static void Print(LibraryGroup group, int depth)
    {
        var abbreviation = group.Abbreviation is null ? string.Empty : $"  [{group.Abbreviation}]";
        Console.WriteLine($"{new string(' ', depth * 2)}{group.Name}{abbreviation}  ({group.Snippets.Count} snippets)");
        foreach (var child in group.Groups)
        {
            Print(child, depth + 1);
        }
    }

    private static IEnumerable<LibraryGroup> Flatten(LibraryGroup group) => group.Groups.SelectMany(Flatten).Prepend(group);
}
