using TextFlow.Core.Diagnostics;
using TextFlow.Core.Import;
using TextFlow.Core.Library;
using TextFlow.Core.Menus;
using TextFlow.Infrastructure.Storage;

namespace TextFlow.App.Engine;

/// <summary>What the Inicio page shows about the library. Counts only, plus a load error for the user.</summary>
/// <param name="SourcePath">aText backup the library is imported from (null once TextFlow is the only editor).</param>
/// <param name="Summary">The library in the database; kept when an import fails.</param>
/// <param name="Error">Why the last import failed (shown in the window, never logged).</param>
public sealed record LibraryStatus(string? SourcePath, LibrarySummary? Summary, string? Error);

/// <summary>
/// The library lives in SQLite (H2.2). Until TextFlow has its own editor (H3), aText is still where the user edits
/// snippets, so its backup file is imported on first start and again whenever it changes. Every import is preceded
/// by a snapshot of the database (H2.5 restores them).
/// </summary>
public sealed class LibraryHost : IAsyncDisposable
{
    /// <summary>aText writes the backup in several steps: wait for it to settle before reading.</summary>
    private static readonly TimeSpan ReimportDelay = TimeSpan.FromSeconds(1);

    private readonly IDiagnosticSink _sink;
    private readonly SqliteDatabase _db;
    private readonly LibraryBackups _backups;
    private readonly Timer _reimportTimer;
    private FileSystemWatcher? _watcher;
    private int _lastImportIssues;
    private bool _disposed;

    public LibraryHost(AppPaths paths, IDiagnosticSink sink)
    {
        _sink = sink;
        _db = new SqliteDatabase(paths.Database);
        _backups = new LibraryBackups(_db, paths.Backups);
        Service = new LibraryService(new SqliteLibraryRepository(_db, TimeProvider.System));
        Service.Changed += root => Publish(root, error: null);
        _reimportTimer = new Timer(_ => _ = ReimportFromWatcherAsync(), null, Timeout.Infinite, Timeout.Infinite);
        Status = new LibraryStatus(null, null, null);
    }

    /// <summary>Raised (any thread) when <see cref="Status"/> changed.</summary>
    public event Action? StatusChanged;

    public LibraryService Service { get; }

    public LibraryStatus Status { get; private set; }

    /// <summary>Opens (and migrates) the database; on first start imports the configured aText backup.</summary>
    public async Task<LibraryGroup> InitializeAsync(string? aTextPath, CancellationToken ct)
    {
        await _db.InitializeAsync(ct).ConfigureAwait(false);
        var root = await Service.LoadAsync(ct).ConfigureAwait(false);
        Status = Status with { SourcePath = NullIfBlank(aTextPath) };

        if (Service.IsEmpty && Status.SourcePath is { } path)
        {
            await ImportAsync(path, ct).ConfigureAwait(false); // one-time move from the .atext file into SQLite
        }
        else
        {
            Publish(root, error: null);
        }

        _watcher = Watch(Status.SourcePath);
        return Service.Current;
    }

    /// <summary>Reads a backup and reports what importing it would change, without writing anything (H2.3).</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="InvalidDataException">The file is not an aText backup.</exception>
    public async Task<(ATextImport Import, ImportPreview Preview)> PreviewATextAsync(string path, CancellationToken ct)
    {
        var import = await ReadAsync(path, ct).ConfigureAwait(false);
        return (import, ImportPreview.Of(import, Service.Current));
    }

    /// <summary>Applies a previewed backup (snapshot first) and follows that file from now on.</summary>
    public async Task UseATextSourceAsync(string path, ATextImport import, CancellationToken ct)
    {
        _watcher?.Dispose();
        Status = Status with { SourcePath = path };
        await ApplyAsync(import, ct).ConfigureAwait(false);
        _watcher = Watch(path);
    }

    /// <summary>Largest TextFlow library file accepted (the user's is ~300 KB): guards against reading a wrong huge file.</summary>
    private const long MaxLibraryFileBytes = 50L * 1024 * 1024;

    /// <summary>Writes the current library as TextFlow JSON (H2.4); a crash mid-write never leaves a half file.</summary>
    public async Task ExportAsync(string path, CancellationToken ct)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, LibraryJson.Export(Service.Current), ct).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Reads a TextFlow library file and reports what importing it would change, without writing anything.</summary>
    /// <exception cref="InvalidDataException">Not a TextFlow library, a newer format, too big or inconsistent.</exception>
    public async Task<(LibraryGroup Root, ImportPreview Preview)> PreviewLibraryFileAsync(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > MaxLibraryFileBytes)
        {
            throw new InvalidDataException("El archivo es demasiado grande para ser una biblioteca de TextFlow.");
        }

        var root = LibraryJson.Import(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        return (root, ImportPreview.Of(new ATextImport(root, []), Service.Current));
    }

    /// <summary>
    /// Replaces the library with a TextFlow file (snapshot first) and stops following aText: otherwise the next change
    /// to the .atext file would overwrite what was just imported.
    /// </summary>
    public async Task ImportLibraryFileAsync(LibraryGroup root, CancellationToken ct)
    {
        _watcher?.Dispose();
        _watcher = null;
        Status = Status with { SourcePath = null };
        await ApplyAsync(new ATextImport(root, []), ct).ConfigureAwait(false);
    }

    /// <summary>TextFlow becomes the editor: changes to the aText file are no longer imported.</summary>
    public void StopFollowingATextSource()
    {
        _watcher?.Dispose();
        _watcher = null;
        Status = Status with { SourcePath = null };
        StatusChanged?.Invoke();
    }

    /// <summary>Reads the followed aText backup again.</summary>
    public Task ReimportAsync(CancellationToken ct) =>
        Status.SourcePath is { } path ? ImportAsync(path, ct) : Task.CompletedTask;

    /// <summary>A failed read or import leaves the library as it was and reports why.</summary>
    private async Task ImportAsync(string path, CancellationToken ct)
    {
        ATextImport import;
        try
        {
            import = await ReadAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Fail(ex);
            return;
        }

        await ApplyAsync(import, ct).ConfigureAwait(false);
    }

    private static async Task<ATextImport> ReadAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct).ConfigureAwait(false); // read fast, parse off the file handle
        buffer.Position = 0;
        return ATextBackupReader.Read(buffer);
    }

    private async Task ApplyAsync(ATextImport import, CancellationToken ct)
    {
        if (!Service.IsEmpty)
        {
            await BackupAsync(ct).ConfigureAwait(false);
        }

        _lastImportIssues = import.Issues.Count;
        await Service.ImportAsync(import.Root, ct).ConfigureAwait(false); // raises Changed → Publish
    }

    private Task<LibraryBackup> BackupAsync(CancellationToken ct) => _backups.CreateAsync(DateTimeOffset.Now, ct);

    /// <summary>Snapshots taken before imports and restores, newest first (H2.5).</summary>
    public IReadOnlyList<LibraryBackup> ListBackups() => _backups.List();

    /// <summary>Reads a snapshot (untouched) and reports what restoring it would change.</summary>
    /// <exception cref="InvalidDataException">The file is not a TextFlow database.</exception>
    public async Task<(LibraryGroup Root, ImportPreview Preview)> PreviewBackupAsync(LibraryBackup backup, CancellationToken ct)
    {
        var root = await LibraryBackups.ReadAsync(backup.Path, ct).ConfigureAwait(false);
        return (root, ImportPreview.Of(new ATextImport(root, []), Service.Current));
    }

    private void Publish(LibraryGroup root, string? error)
    {
        var index = LibraryIndex.Build(root);
        var summary = LibrarySummary.Of(root, index, _lastImportIssues);
        _sink.Record(new LibraryLoaded(DateTimeOffset.UtcNow, summary.Menus, summary.Commands, summary.DirectTriggers, summary.Issues));
        Status = Status with { Summary = summary, Error = error };
        StatusChanged?.Invoke();
    }

    private void Fail(Exception ex)
    {
        _sink.Record(new EngineFault(DateTimeOffset.UtcNow, ex.GetType().Name));
        Status = Status with { Error = ex.Message };
        StatusChanged?.Invoke();
    }

    private async Task ReimportFromWatcherAsync()
    {
        try
        {
            await ReimportAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            if (!_disposed)
            {
                Fail(ex);
            }
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
        FileSystemEventHandler changed = (_, _) => _reimportTimer.Change(ReimportDelay, Timeout.InfiniteTimeSpan); // debounce
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Renamed += (_, _) => _reimportTimer.Change(ReimportDelay, Timeout.InfiniteTimeSpan);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        await _reimportTimer.DisposeAsync().ConfigureAwait(false);
        Service.Dispose();
    }
}
