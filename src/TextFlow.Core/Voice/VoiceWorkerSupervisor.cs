using TextFlow.Contracts.Voice;
using TextFlow.Core.Diagnostics;

namespace TextFlow.Core.Voice;

/// <summary>
/// Keeps one <c>TextFlow.VoiceWorker</c> running (V0.2 H1.1, spec §7): starts it, checks it with a heartbeat, kills it
/// when it hangs and restarts it with a growing delay after any crash. Gives up (<see cref="VoiceWorkerState.Faulted"/>)
/// after <see cref="VoiceWorkerOptions.MaxCrashes"/> crashes in <see cref="VoiceWorkerOptions.CrashWindow"/>.
/// Everything runs on the thread pool, in another process: expansions never wait for it.
/// </summary>
public sealed class VoiceWorkerSupervisor : IAsyncDisposable
{
    private readonly IVoiceWorkerLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly IDiagnosticSink _sink;
    private readonly VoiceWorkerOptions _options;
    private readonly Lock _gate = new();
    private readonly List<DateTimeOffset> _crashes = [];
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private TaskCompletionSource? _retry;
    private VoiceWorkerSession? _session;
    private VoiceWorkerState _state = VoiceWorkerState.Stopped;

    public VoiceWorkerSupervisor(
        IVoiceWorkerLauncher launcher, IDiagnosticSink sink, TimeProvider? time = null, VoiceWorkerOptions? options = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _time = time ?? TimeProvider.System;
        _options = options ?? new VoiceWorkerOptions();
    }

    /// <summary>Raised on a pool thread; marshal before touching UI.</summary>
    public event Action<VoiceWorkerState>? StateChanged;

    public VoiceWorkerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Starts the worker; also the way out of <see cref="VoiceWorkerState.Faulted"/>. No-op while running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_retry is { } retry)
            {
                _retry = null;
                retry.TrySetResult();
                return;
            }

            if (_run is { IsCancellationRequested: false })
            {
                return;
            }

            // A StopAsync may still be tearing the previous worker down: the new loop starts after it.
            var previous = _loop;
            _run = new CancellationTokenSource();
            var token = _run.Token;
            _loop = Task.Run(
                async () =>
                {
                    await previous.ConfigureAwait(false); // RunAsync never throws
                    await RunAsync(token).ConfigureAwait(false);
                },
                CancellationToken.None);
        }
    }

    /// <summary>Asks the worker to exit (killing it if it does not) and stops restarting it.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? run;
        Task loop;
        lock (_gate)
        {
            run = _run;
            loop = _loop;
            _retry = null;
        }

        if (run is null)
        {
            return;
        }

        // _run stays set until the loop is over, so a concurrent Start waits for it instead of racing it.
        // The CTS is not disposed: a concurrent StopAsync may still cancel it (it has no timer to free).
        await run.CancelAsync().ConfigureAwait(false);
        await loop.ConfigureAwait(false); // RunAsync never throws
        bool stopped;
        lock (_gate)
        {
            stopped = _run == run;
            if (stopped)
            {
                _run = null;
            }
        }

        if (stopped)
        {
            SetState(VoiceWorkerState.Stopped);
        }
    }

    /// <summary>Round trip to the worker.</summary>
    /// <exception cref="VoiceWorkerUnavailableException">Not running, or it went away while waiting.</exception>
    /// <exception cref="TimeoutException">It did not answer in <see cref="VoiceWorkerOptions.HeartbeatTimeout"/>.</exception>
    public Task<TimeSpan> PingAsync(CancellationToken cancellationToken)
    {
        var session = Volatile.Read(ref _session) ?? throw new VoiceWorkerUnavailableException();
        return session.PingAsync(_options.HeartbeatTimeout, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var reason = await RunOnceAsync(stop).ConfigureAwait(false);
                if (reason == VoiceWorkerExitReason.Stopped)
                {
                    return;
                }

                await WaitBeforeRestartAsync(stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // StopAsync: normal end.
        }
        catch (Exception ex)
        {
            // A bug here must not take the app down: report it and stay faulted; Start() launches a new loop.
            _sink.Record(new EngineFault(_time.GetUtcNow(), ex.GetType().Name));
            lock (_gate)
            {
                if (_run?.Token == stop)
                {
                    _run = null;
                }
            }

            SetState(VoiceWorkerState.Faulted);
        }
    }

    /// <summary>One worker lifetime, from launch to exit.</summary>
    private async Task<VoiceWorkerExitReason> RunOnceAsync(CancellationToken stop)
    {
        SetState(VoiceWorkerState.Starting);
        var launchedAt = _time.GetTimestamp();

        IVoiceWorkerConnection connection;
        try
        {
            connection = await _launcher.LaunchAsync(stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            RecordExit(VoiceWorkerExitReason.LaunchFailed, null, launchedAt);
            return VoiceWorkerExitReason.LaunchFailed;
        }

        await using (connection.ConfigureAwait(false))
        {
            VoiceWorkerSession session;
            try
            {
                session = await VoiceWorkerSession.OpenAsync(connection.Stream, _time, _options.HandshakeTimeout, stop)
                    .ConfigureAwait(false);
            }
            catch (VoiceHandshakeException)
            {
                var exitCode = await EndAsync(connection, graceful: false).ConfigureAwait(false);
                RecordExit(VoiceWorkerExitReason.HandshakeFailed, exitCode, launchedAt);
                return VoiceWorkerExitReason.HandshakeFailed;
            }
            catch (OperationCanceledException)
            {
                await EndAsync(connection, graceful: false).ConfigureAwait(false);
                throw;
            }

            _sink.Record(new VoiceWorkerStarted(
                _time.GetUtcNow(), connection.ProcessId, _time.GetElapsedTime(launchedAt).TotalMilliseconds));
            var readyAt = _time.GetTimestamp();
            Volatile.Write(ref _session, session);
            SetState(VoiceWorkerState.Ready);

            using (session)
            {
                VoiceWorkerExitReason reason;
                try
                {
                    reason = await WatchAsync(connection, session, stop).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _session, null);
                }

                var code = await EndAsync(connection, graceful: reason == VoiceWorkerExitReason.Stopped, session)
                    .ConfigureAwait(false);
                RecordExit(reason, code, readyAt);
                return reason;
            }
        }
    }

    /// <summary>Waits until the worker exits, closes its pipe, hangs, or TextFlow stops it.</summary>
    private async Task<VoiceWorkerExitReason> WatchAsync(
        IVoiceWorkerConnection connection, VoiceWorkerSession session, CancellationToken stop)
    {
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var heartbeat = HeartbeatAsync(session, watch.Token);
        var stopped = Task.Delay(Timeout.Infinite, watch.Token);

        var first = await Task.WhenAny(session.Completion, connection.Exited, heartbeat, stopped).ConfigureAwait(false);
        await watch.CancelAsync().ConfigureAwait(false);

        if (stop.IsCancellationRequested)
        {
            return VoiceWorkerExitReason.Stopped;
        }

        if (first == heartbeat)
        {
            return await heartbeat.ConfigureAwait(false);
        }

        return first == session.Completion ? await session.Completion.ConfigureAwait(false) : VoiceWorkerExitReason.Crashed;
    }

    /// <summary>Returns only when a ping fails: <see cref="VoiceWorkerExitReason.Hung"/> if it timed out.</summary>
    private async Task<VoiceWorkerExitReason> HeartbeatAsync(VoiceWorkerSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.HeartbeatInterval, _time, cancellationToken).ConfigureAwait(false);
                await session.PingAsync(_options.HeartbeatTimeout, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            return VoiceWorkerExitReason.Hung;
        }
        catch (Exception ex) when (ex is VoiceWorkerUnavailableException or IOException or OperationCanceledException)
        {
            return VoiceWorkerExitReason.Crashed;
        }
    }

    /// <summary>Ends the process (politely first when <paramref name="graceful"/>) and returns its exit code if known.</summary>
    private async Task<int?> EndAsync(IVoiceWorkerConnection connection, bool graceful, VoiceWorkerSession? session = null)
    {
        if (graceful && session is not null && !connection.Exited.IsCompleted)
        {
            try
            {
                await session.SendAsync(new ShutdownMessage(), CancellationToken.None)
                    .WaitAsync(_options.ShutdownTimeout, _time).ConfigureAwait(false);
                await connection.Exited.WaitAsync(_options.ShutdownTimeout, _time).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                // Did not leave on its own: killed below.
            }
        }

        if (!connection.Exited.IsCompleted)
        {
            connection.Kill();
        }

        try
        {
            return await connection.Exited.WaitAsync(_options.ShutdownTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private async Task WaitBeforeRestartAsync(CancellationToken stop)
    {
        int crashes;
        TaskCompletionSource? retry = null;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _crashes.Add(now);
            _crashes.RemoveAll(at => now - at > _options.CrashWindow);
            crashes = _crashes.Count;
            if (crashes >= _options.MaxCrashes)
            {
                retry = _retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _crashes.Clear();
            }
        }

        if (retry is not null)
        {
            _sink.Record(new VoiceWorkerFaulted(_time.GetUtcNow(), crashes));
            SetState(VoiceWorkerState.Faulted);
            await retry.Task.WaitAsync(stop).ConfigureAwait(false);
            return;
        }

        SetState(VoiceWorkerState.Restarting);
        await Task.Delay(RestartDelay(crashes), _time, stop).ConfigureAwait(false);
    }

    private TimeSpan RestartDelay(int crashes)
    {
        var delay = _options.FirstRestartDelay * Math.Pow(2, Math.Max(0, crashes - 1));
        return delay < _options.MaxRestartDelay ? delay : _options.MaxRestartDelay;
    }

    private void RecordExit(VoiceWorkerExitReason reason, int? exitCode, long since) =>
        _sink.Record(new VoiceWorkerExited(_time.GetUtcNow(), reason, exitCode, _time.GetElapsedTime(since).TotalSeconds));

    private void SetState(VoiceWorkerState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            // A faulty subscriber must not kill the loop.
            _sink.Record(new EngineFault(_time.GetUtcNow(), ex.GetType().Name));
        }
    }
}
