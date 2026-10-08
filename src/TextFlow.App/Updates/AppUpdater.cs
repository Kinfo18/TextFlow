using Velopack;
using Velopack.Sources;

namespace TextFlow.App.Updates;

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
    private readonly Action<Exception> _recordFault;
    private UpdateInfo? _ready;

    public AppUpdater(Action<Exception> recordFault) => _recordFault = recordFault;

    /// <summary>Raised on a thread-pool thread when an update has been downloaded.</summary>
    public event Action? UpdateReady;

    public bool IsInstalled => _manager.IsInstalled;

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

    private async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return;
            }

            await _manager.DownloadUpdatesAsync(update, progress: null, cancelToken: ct).ConfigureAwait(false);
            _ready = update;
            UpdateReady?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _recordFault(ex); // offline or GitHub unreachable: try again at the next interval
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
