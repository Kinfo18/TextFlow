using TextFlow.Core.Diagnostics;
using TextFlow.Core.Engine;
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

namespace TextFlow.App.Engine;

/// <summary>What the library load produced; shown on the Inicio page. Counts only, no content.</summary>
public sealed record LibraryStatus(int Menus, int Commands, string? Error)
{
    public static LibraryStatus NotConfigured { get; } = new(0, 0, null);
}

/// <summary>
/// Owns the real expansion pipeline (hook, resolver, insertion, chime) around <see cref="ExpansionEngine"/>
/// and runs it in the background. Pause/resume may be called from any thread.
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    private readonly KeyboardHook _hook;
    private readonly ClipboardStrategy _clipboard;
    private readonly ExpansionEngine _engine;
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private bool _disposed;

    public EngineHost(AppSettings settings, IMenuPresenter menu, IDiagnosticSink sink)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        var resolver = new Win32TargetResolver(new UiaFocusedControlInspector());
        _clipboard = new ClipboardStrategy();
        _hook = new KeyboardHook(new TriggerMatcher([], TriggerOptions.Default));
        _engine = new ExpansionEngine(
            _hook,
            resolver,
            new SecurityPolicy(BuiltinExclusions.All),
            new InsertionCoordinator(resolver, [_clipboard, new SendInputStrategy()], new InsertionOptions()),
            menu,
            new ExpansionSound(settings.ChimeVolume),
            new CursorPointerLocator(),
            sink,
            TimeProvider.System,
            ExpansionEngineOptions.Default);
    }

    /// <summary>Raised (any thread) with the new paused state.</summary>
    public event Action<bool>? PausedChanged;

    public AppSettings Settings { get; }

    public LibraryStatus Library { get; private set; } = LibraryStatus.NotConfigured;

    public bool IsPaused => _engine.IsPaused;

    public async Task StartAsync()
    {
        Library = await LoadLibraryAsync().ConfigureAwait(false);
        _run = _engine.RunAsync(_stop.Token);
    }

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

    private async Task<LibraryStatus> LoadLibraryAsync()
    {
        var index = LibraryIndex.Build(new ImportedGroup("root", "root", null, true, [], []));
        var status = LibraryStatus.NotConfigured;

        if (Settings.ATextBackupPath is { Length: > 0 } path)
        {
            try
            {
                await using var file = File.OpenRead(path);
                index = LibraryIndex.Build(ATextBackupReader.Read(file).Root);
                status = new LibraryStatus(index.Menus.Count, index.Triggers.Count - index.Menus.Count, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                status = new LibraryStatus(0, 0, ex.Message); // the engine still runs, just with nothing to expand
            }
        }

        await _engine.LoadAsync(index).ConfigureAwait(false);
        return status;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
