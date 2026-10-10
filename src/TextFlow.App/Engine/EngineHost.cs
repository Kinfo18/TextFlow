using TextFlow.Contracts.Targeting;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Engine;
using TextFlow.Core.Expansion;
using TextFlow.Core.Input;
using TextFlow.Core.Library;
using TextFlow.Core.Menus;
using TextFlow.Core.Operations;
using TextFlow.Core.Security;
using TextFlow.Infrastructure.Clipboard;
using TextFlow.Infrastructure.Feedback;
using TextFlow.Infrastructure.Hooks;
using TextFlow.Infrastructure.Input;
using TextFlow.Infrastructure.Targeting;

namespace TextFlow.App.Engine;

/// <summary>
/// Owns the real expansion pipeline (hook, resolver, insertion, chime, hook watchdog) around
/// <see cref="ExpansionEngine"/> and runs it in the background. The library comes from <see cref="LibraryHost"/>.
/// Pause/resume may be called from any thread.
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    private readonly KeyboardHook _hook;
    private readonly ClipboardStrategy _clipboard;
    private readonly ExpansionEngine _engine;
    private readonly IDiagnosticSink _sink;
    private readonly CancellationTokenSource _stop = new();
    private readonly HookWatchdog _watchdog;
    private readonly ExpansionSound _sound;
    private Task? _run;
    private bool _disposed;

    private static readonly string SelfProcess = Path.GetFileName(Environment.ProcessPath) ?? "TextFlow.exe";

    public EngineHost(AppSettings settings, IMenuPresenter menu, IFieldPrompt fieldPrompt, IDiagnosticSink sink, IUsageRecorder? usage = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var soundEnabled = settings.SoundEnabled;
        var chimeVolume = settings.ChimeVolume;
        _sink = sink;
        _sound = new ExpansionSound(chimeVolume) { Enabled = soundEnabled };
        var resolver = new Win32TargetResolver(new UiaFocusedControlInspector());
        _clipboard = new ClipboardStrategy();
        _hook = new KeyboardHook(new TriggerMatcher([], TriggerOptions.Default));
        _engine = new ExpansionEngine(
            _hook,
            resolver,
            Policy(settings.ExcludedProcesses ?? []),
            new InsertionCoordinator(resolver, [_clipboard, new SendInputStrategy()], new InsertionOptions()),
            menu,
            _sound,
            new CursorPointerLocator(),
            sink,
            TimeProvider.System,
            ExpansionEngineOptions.Default with { PendingTimeout = PendingTimeout(settings.PrefixTimeoutMs) },
            fieldPrompt,
            new ClipboardVariables(_clipboard, TimeProvider.System),
            usage);
        _watchdog = new HookWatchdog(_hook, InputProbe.LastInputTick, InputProbe.ForegroundBlocksHooks, TimeProvider.System, HookWatchdogOptions.Default);
        _watchdog.Reinstalled += info =>
        {
            HookReinstalls = info.TimesThisSession;
            _sink.Record(new HookReinstalled(
                DateTimeOffset.UtcNow, info.TimesThisSession, info.MissedInputMs, _hook.Stats.MaxCallbackMs, InputProbe.ForegroundProcessName()));
        };
        _watchdog.ReinstallFailed += ex => _sink.Record(new EngineFault(DateTimeOffset.UtcNow, ex.GetType().Name));
    }

    /// <summary>Raised (any thread) with the new paused state.</summary>
    public event Action<bool>? PausedChanged;

    public bool IsPaused => _engine.IsPaused;

    /// <summary>Times the watchdog had to install the hook again this session (R3).</summary>
    public int HookReinstalls { get; private set; }

    public async Task StartAsync(LibraryGroup library)
    {
        await UseLibraryAsync(library).ConfigureAwait(false);
        _run = _engine.RunAsync(_stop.Token);
    }

    /// <summary>Installs a new library at once (startup, import, edit): its triggers replace the previous ones.</summary>
    public Task UseLibraryAsync(LibraryGroup library) => _engine.LoadAsync(LibraryIndex.Build(library));

    /// <summary>Clamps a stored value into the range the engine accepts (settings.json may be edited by hand).</summary>
    public static TimeSpan PendingTimeout(int milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Clamp(
            milliseconds, ExpansionEngine.MinPendingTimeout.TotalMilliseconds, ExpansionEngine.MaxPendingTimeout.TotalMilliseconds));

    public void SetPendingTimeout(int milliseconds) => _engine.SetPendingTimeout(PendingTimeout(milliseconds));

    /// <summary>Built-in exclusions and TextFlow itself always apply; <paramref name="processes"/> come from Configuración.</summary>
    public void SetExclusions(IEnumerable<string> processes) => _engine.UsePolicy(Policy(processes));

    private static SecurityPolicy Policy(IEnumerable<string> processes) =>
        new([.. BuiltinExclusions.For(SelfProcess), .. UserExclusions.Rules(processes)]);

    /// <summary>Applies the Configuración sound choices at once (any thread).</summary>
    public void ConfigureSound(bool enabled, double volume)
    {
        _sound.Enabled = enabled;
        if (_sound.Volume != volume)
        {
            _sound.Volume = volume;
        }
    }

    /// <summary>"Probar" in Configuración: the same chime an expansion plays. False if off or no audio device.</summary>
    public bool PlayChime() => _sound.Play();

    /// <summary>Wakes a sleeping audio device (inaudible) before the user presses "Probar".</summary>
    public void WarmSound() => _sound.Warm();

    /// <summary>Command palette (D11): the field to come back to, captured before the palette takes the focus.</summary>
    public Task<ActiveTarget?> CapturePaletteTargetAsync() => _engine.CapturePaletteTargetAsync();

    /// <summary>Command palette: insert this snippet into the captured field.</summary>
    public void InsertFromPalette(string snippetId, ActiveTarget target) => _engine.InsertFromPalette(snippetId, target);

    public void TogglePause()
    {
        if (_engine.IsPaused)
        {
            _engine.Resume();
        }
        else
        {
            _engine.Pause();
        }

        PausedChanged?.Invoke(_engine.IsPaused);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchdog.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            await _run.ConfigureAwait(false);
        }

        _hook.Dispose();
        _clipboard.Dispose();
        _stop.Dispose();
    }
}
