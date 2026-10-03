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
    private Task? _run;
    private bool _disposed;

    public EngineHost(double chimeVolume, IMenuPresenter menu, IDiagnosticSink sink)
    {
        _sink = sink;
        var resolver = new Win32TargetResolver(new UiaFocusedControlInspector());
        _clipboard = new ClipboardStrategy();
        _hook = new KeyboardHook(new TriggerMatcher([], TriggerOptions.Default));
        _engine = new ExpansionEngine(
            _hook,
            resolver,
            new SecurityPolicy(BuiltinExclusions.For(Path.GetFileName(Environment.ProcessPath) ?? "TextFlow.exe")),
            new InsertionCoordinator(resolver, [_clipboard, new SendInputStrategy()], new InsertionOptions()),
            menu,
            new ExpansionSound(chimeVolume),
            new CursorPointerLocator(),
            sink,
            TimeProvider.System,
            ExpansionEngineOptions.Default);
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
