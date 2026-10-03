using TextFlow.Core.Diagnostics;
using TextFlow.Core.Engine;
using TextFlow.Core.Expansion;
using TextFlow.Core.Import;
using TextFlow.Core.Input;
using TextFlow.Core.Menus;
using TextFlow.Core.Operations;
using TextFlow.Core.Security;
using TextFlow.Infrastructure.Clipboard;
using TextFlow.Infrastructure.Feedback;
using TextFlow.Infrastructure.Hooks;
using TextFlow.Infrastructure.Input;
using TextFlow.Infrastructure.Targeting;
using TextFlow.Core.Library;

namespace TextFlow.App.Engine;

/// <summary>What the Inicio page shows about the library. Counts only, plus a load error for the user.</summary>
/// <param name="Summary">Last library that loaded; kept when a later reload fails.</param>
/// <param name="Error">Why the last load failed (shown in the window, never logged).</param>
public sealed record LibraryStatus(string? Path, LibrarySummary? Summary, string? Error)
{
    public static LibraryStatus NotConfigured { get; } = new(null, null, null);
}

/// <summary>
/// Owns the real expansion pipeline (hook, resolver, insertion, chime) around <see cref="ExpansionEngine"/>,
/// runs it in the background and keeps the aText library in sync with its file (H1.5, provisional until the
/// SQLite library of H2). Pause/resume may be called from any thread.
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    /// <summary>aText writes the backup in several steps: wait for it to settle before reading.</summary>
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromSeconds(1);

    private readonly KeyboardHook _hook;
    private readonly ClipboardStrategy _clipboard;
    private readonly ExpansionEngine _engine;
    private readonly IDiagnosticSink _sink;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Timer _reloadTimer;
    private readonly HookWatchdog _watchdog;
    private FileSystemWatcher? _watcher;
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
        _watchdog.Reinstalled += count =>
        {
            HookReinstalls = count;
            _sink.Record(new HookReinstalled(DateTimeOffset.UtcNow, count));
        };
        _watchdog.ReinstallFailed += ex => _sink.Record(new EngineFault(DateTimeOffset.UtcNow, ex.GetType().Name));
        _reloadTimer = new Timer(_ => _ = ReloadFromWatcherAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised (any thread) with the new paused state.</summary>
    public event Action<bool>? PausedChanged;

    /// <summary>Raised (any thread) after every load attempt, successful or not.</summary>
    public event Action? LibraryChanged;

    public LibraryStatus Library { get; private set; } = LibraryStatus.NotConfigured;

    public bool IsPaused => _engine.IsPaused;

    /// <summary>Times the watchdog had to install the hook again this session (R3).</summary>
    public int HookReinstalls { get; private set; }

    public async Task StartAsync(string? libraryPath)
    {
        await UseLibraryAsync(libraryPath).ConfigureAwait(false);
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

    /// <summary>Switches to another backup (or none) and watches it for changes.</summary>
    public async Task UseLibraryAsync(string? path)
    {
        _watcher?.Dispose();
        _watcher = null;
        Library = new LibraryStatus(string.IsNullOrWhiteSpace(path) ? null : path, null, null);
        await ReloadAsync().ConfigureAwait(false);
        _watcher = Watch(Library.Path);
    }

    /// <summary>Reads the current backup again. A failed read keeps the library that is already loaded.</summary>
    public async Task ReloadAsync()
    {
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Library = await LoadAsync(Library).ConfigureAwait(false);
        }
        finally
        {
            _loadGate.Release();
        }

        LibraryChanged?.Invoke();
    }

    private async Task ReloadFromWatcherAsync()
    {
        try
        {
            await ReloadAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The file changed while TextFlow was exiting.
        }
    }

    private async Task<LibraryStatus> LoadAsync(LibraryStatus current)
    {
        if (current.Path is not { } path)
        {
            await _engine.LoadAsync(LibraryIndex.Build(new LibraryGroup("root", "root", null, true, [], []))).ConfigureAwait(false);
            return LibraryStatus.NotConfigured;
        }

        try
        {
            ATextImport import;
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                import = ATextBackupReader.Read(file);
            }

            var index = LibraryIndex.Build(import.Root);
            await _engine.LoadAsync(index).ConfigureAwait(false);
            var summary = LibrarySummary.Of(import, index);
            _sink.Record(new LibraryLoaded(DateTimeOffset.UtcNow, summary.Menus, summary.Commands, summary.DirectTriggers, summary.Issues));
            return new LibraryStatus(path, summary, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _sink.Record(new EngineFault(DateTimeOffset.UtcNow, ex.GetType().Name));
            return current with { Error = ex.Message }; // keep expanding with what was loaded before
        }
    }

    private FileSystemWatcher? Watch(string? path)
    {
        if (path is null || Path.GetDirectoryName(Path.GetFullPath(path)) is not { } directory || !Directory.Exists(directory))
        {
            return null;
        }

        var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        FileSystemEventHandler changed = (_, _) => _reloadTimer.Change(ReloadDelay, Timeout.InfiniteTimeSpan); // debounce bursts
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Renamed += (_, _) => _reloadTimer.Change(ReloadDelay, Timeout.InfiniteTimeSpan);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchdog.Dispose();
        _watcher?.Dispose();
        await _reloadTimer.DisposeAsync().ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            await _run.ConfigureAwait(false);
        }

        _hook.Dispose();
        _clipboard.Dispose();
        _stop.Dispose();
        _loadGate.Dispose();
    }
}
