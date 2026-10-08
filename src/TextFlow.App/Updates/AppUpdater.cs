using Velopack;
using Velopack.Sources;

namespace TextFlow.App.Updates;

internal enum UpdateCheck
{
    /// <summary>Portable or dev copy: updates come only from installing a new version.</summary>
    NotInstalled,
    UpToDate,
    /// <summary>Downloaded: "Reiniciar y actualizar" applies it.</summary>
    Ready,
    /// <summary>Offline or GitHub unreachable.</summary>
    Failed,
}

/// <summary>
/// Updates from GitHub Releases (ADR-0004, Velopack). Checks in the background, downloads the update and waits for
/// the user: "Reiniciar y actualizar" on Inicio or in the tray. Does nothing when not installed (portable zip, dev build).
/// </summary>
internal sealed class AppUpdater : IDisposable
{
    private const string ReleasesRepository = "https://github.com/Kinfo18/TextFlow";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30); // keep startup fast
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager _manager = new(new GithubSource(ReleasesRepository, accessToken: null, prerelease: false));
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _checking = new(1, 1); // background and "Buscar actualizaciones" never overlap
    private readonly Action<Exception> _recordFault;
    private UpdateInfo? _ready;

    public AppUpdater(Action<Exception> recordFault) => _recordFault = recordFault;

    /// <summary>Raised on a thread-pool thread when an update has been downloaded.</summary>
    public event Action? UpdateReady;

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>Installed version, or the build's own for a portable or dev copy.</summary>
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? AppVersion.Display;

    /// <summary>Version downloaded and waiting for a restart, if any.</summary>
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    public void Start()
    {
        if (IsInstalled)
        {
            _ = RunAsync(_stopping.Token);
        }
    }

    /// <summary>Hands the update to Velopack, which applies it once TextFlow has exited and starts it again.</summary>
    /// <returns>False when there is nothing to apply.</returns>
    public bool ApplyOnExit()
    {
        if (_ready is null)
        {
            return false;
        }

        _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: true);
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(CheckInterval);
            do
            {
                await CheckAsync(ct).ConfigureAwait(false);
            }
            while (_ready is null && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // exiting
        }
    }

    /// <summary>"Buscar actualizaciones": checks now and downloads what it finds.</summary>
    /// <param name="progress">Download percentage (0–100), raised on a thread-pool thread.</param>
    public Task<UpdateCheck> CheckNowAsync(Action<int>? progress = null) =>
        IsInstalled ? CheckAsync(_stopping.Token, progress) : Task.FromResult(UpdateCheck.NotInstalled);

    private async Task<UpdateCheck> CheckAsync(CancellationToken ct, Action<int>? progress = null)
    {
        await _checking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready is not null)
            {
                return UpdateCheck.Ready;
            }

            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return UpdateCheck.UpToDate;
            }

            await _manager.DownloadUpdatesAsync(update, progress, cancelToken: ct).ConfigureAwait(false);
            _ready = update;
            UpdateReady?.Invoke();
            return UpdateCheck.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _recordFault(ex); // offline or GitHub unreachable: try again at the next interval
            return UpdateCheck.Failed;
        }
        finally
        {
            _checking.Release();
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _checking.Dispose();
    }
}
