using TextFlow.Contracts.Voice;
using TextFlow.Core.Diagnostics;

namespace TextFlow.Core.Voice;

/// <summary>
/// Keeps one <c>TextFlow.VoiceWorker</c> running (V0.2 H1.1, spec §7): starts it, checks it with a heartbeat, kills it
/// when it hangs and restarts it with a growing delay after any crash. Gives up (<see cref="VoiceWorkerState.Faulted"/>)
/// after <see cref="VoiceWorkerOptions.MaxCrashes"/> crashes in <see cref="VoiceWorkerOptions.CrashWindow"/>.
/// The speech model loads on demand (<see cref="EnsureModelAsync"/>) and, unused for
/// <see cref="VoiceWorkerOptions.ModelIdleTimeout"/>, is freed by replacing the worker with a fresh one (H1.3).
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
    private TaskCompletionSource _changed = NewSignal();
    private TaskCompletionSource<VoiceWorkerExitReason> _abort = new();
    private int _requestsInFlight;
    private long _lastUse;
    private bool _modelLoaded;

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
            if (!StartLocked())
            {
                return;
            }
        }

        // Right away, not when the loop gets to it: a request sent just after Start() waits for the worker.
        RaiseStateChanged(VoiceWorkerState.Starting);
    }

    /// <summary>The body of <see cref="Start"/>, inside the lock; true when it moved the state to Starting.</summary>
    private bool StartLocked()
    {
        if (_retry is { } retry)
        {
            _retry = null;
            retry.TrySetResult();
            _state = VoiceWorkerState.Starting;
            SignalChanged();
            return true;
        }

        if (_run is { IsCancellationRequested: false })
        {
            return false;
        }

        // Before the loop runs, so it cannot be overwritten on top of a Ready the loop already reached.
        var moved = _state != VoiceWorkerState.Starting;
        _state = VoiceWorkerState.Starting;
        SignalChanged();

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
        return moved;
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

    /// <summary>Active recording devices, as the worker sees them now.</summary>
    /// <exception cref="VoiceWorkerUnavailableException">Not running, or it went away while waiting.</exception>
    public async Task<IReadOnlyList<MicrophoneInfo>> ListMicrophonesAsync(CancellationToken cancellationToken)
    {
        var reply = await RequestAsync<MicrophonesReply>(id => new ListMicrophonesRequest(id), _options.RequestTimeout, cancellationToken)
            .ConfigureAwait(false);
        return reply.Microphones;
    }

    /// <summary>Opens the microphone (null: Windows' default now) and returns its name.</summary>
    /// <exception cref="VoiceRequestException">No microphone, access denied, already capturing…</exception>
    public async Task<string> StartCaptureAsync(string? microphoneId, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync<CaptureStartedReply>(
            id => new StartCaptureRequest(id, microphoneId), _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        return reply.MicrophoneName;
    }

    /// <summary>Closes the microphone; the audio stays in the worker.</summary>
    /// <exception cref="VoiceRequestException">Not capturing, or the microphone was lost while recording.</exception>
    public Task<CaptureStoppedReply> StopCaptureAsync(CancellationToken cancellationToken) =>
        RequestAsync<CaptureStoppedReply>(id => new StopCaptureRequest(id), _options.RequestTimeout, cancellationToken);

    /// <summary>
    /// Loads the speech model in the worker unless it is already there (a few ms then). The first time after a start,
    /// a crash or an idle release takes 1–3 s; a load that hangs past <see cref="VoiceWorkerOptions.ModelLoadTimeout"/>
    /// gets the worker killed and restarted, as a hung one.
    /// </summary>
    /// <exception cref="VoiceRequestException">No model file, unreadable model, no GPU.</exception>
    /// <exception cref="VoiceWorkerUnavailableException">Not running, or it went away while loading.</exception>
    /// <exception cref="TimeoutException">The load did not finish in time.</exception>
    public async Task<ModelLoadedReply> EnsureModelAsync(VoiceModelFiles files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        var session = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);

        // Counted as loaded from the request on: a caller that cancels mid-load does not stop the worker loading it,
        // and the idle release must still free it then.
        SetModelLoaded(session, loaded: true);
        try
        {
            var reply = await session.RequestAsync<ModelLoadedReply>(
                id => new LoadModelRequest(id, files.ModelPath, files.GpuPackPath), _options.ModelLoadTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!reply.WasLoaded)
            {
                _sink.Record(new VoiceModelLoaded(_time.GetUtcNow(), reply.Runtime, reply.LoadMs, reply.WarmupMs));
            }

            return reply;
        }
        catch (VoiceRequestException ex)
        {
            SetModelLoaded(session, loaded: false); // a refused load leaves nothing to free
            _sink.Record(new VoiceModelLoadFailed(_time.GetUtcNow(), ex.Code, TimedOut: false));
            throw;
        }
        catch (TimeoutException)
        {
            _sink.Record(new VoiceModelLoadFailed(_time.GetUtcNow(), VoiceErrorCode.Unknown, TimedOut: true));
            Abort(session, VoiceWorkerExitReason.Hung);
            throw;
        }
        finally
        {
            ReleaseSession();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void SetModelLoaded(VoiceWorkerSession session, bool loaded)
    {
        lock (_gate)
        {
            if (_session == session)
            {
                _modelLoaded = loaded;
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<TReply> RequestAsync<TReply>(Func<long, VoiceRequest> create, TimeSpan timeout, CancellationToken cancellationToken)
        where TReply : VoiceReply
    {
        var session = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await session.RequestAsync<TReply>(create, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseSession();
        }
    }

    /// <summary>
    /// The current worker's session, counted as in use until <see cref="ReleaseSession"/>. While a worker is starting
    /// (the first start, or the fresh one replacing an idle worker) it waits for it, so the release is transparent.
    /// </summary>
    private async Task<VoiceWorkerSession> AcquireSessionAsync(CancellationToken cancellationToken)
    {
        var since = _time.GetTimestamp();
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_session is { } session)
                {
                    _requestsInFlight++;
                    _lastUse = _time.GetTimestamp();
                    return session;
                }

                if (_state is not (VoiceWorkerState.Starting or VoiceWorkerState.Ready))
                {
                    throw new VoiceWorkerUnavailableException();
                }

                changed = _changed.Task;
            }

            // Replacing a worker can take three shutdown waits (send, exit, after the kill), then the handshake.
            var left = _options.HandshakeTimeout + (3 * _options.ShutdownTimeout) - _time.GetElapsedTime(since);
            try
            {
                if (left <= TimeSpan.Zero)
                {
                    throw new TimeoutException();
                }

                await changed.WaitAsync(left, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new VoiceWorkerUnavailableException("The voice worker did not come up in time.", ex);
            }
        }
    }

    private void ReleaseSession()
    {
        lock (_gate)
        {
            _requestsInFlight--;
            _lastUse = _time.GetTimestamp();
        }
    }

    /// <summary>Makes <paramref name="session"/> the current one (a new worker, no model loaded yet), or none.</summary>
    private void Publish(VoiceWorkerSession? session)
    {
        lock (_gate)
        {
            _session = session;
            if (session is not null)
            {
                _modelLoaded = false;
                _lastUse = _time.GetTimestamp();
                _abort = new TaskCompletionSource<VoiceWorkerExitReason>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            SignalChanged();
        }
    }

    /// <summary>Ends that worker for <paramref name="reason"/>, if it is still the current one.</summary>
    private void Abort(VoiceWorkerSession session, VoiceWorkerExitReason reason)
    {
        lock (_gate)
        {
            if (_session == session)
            {
                _abort.TrySetResult(reason);
            }
        }
    }

    /// <summary>Wakes the requests waiting in <see cref="AcquireSessionAsync"/>; call inside the lock.</summary>
    private void SignalChanged()
    {
        var changed = _changed;
        _changed = NewSignal();
        changed.TrySetResult();
    }

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

                if (reason == VoiceWorkerExitReason.ModelIdle)
                {
                    continue; // a planned replacement, not a crash: the fresh worker starts right away
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
            Publish(session);
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
                    Publish(null);
                }

                var graceful = reason is VoiceWorkerExitReason.Stopped or VoiceWorkerExitReason.ModelIdle;
                var code = await EndAsync(connection, graceful, session).ConfigureAwait(false);
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
        var idle = IdleAsync(session, watch.Token);
        Task<VoiceWorkerExitReason> aborted;
        lock (_gate)
        {
            aborted = _abort.Task;
        }

        var stopped = Task.Delay(Timeout.Infinite, watch.Token);

        var first = await Task.WhenAny(session.Completion, connection.Exited, heartbeat, idle, aborted, stopped)
            .ConfigureAwait(false);
        await watch.CancelAsync().ConfigureAwait(false);

        if (stop.IsCancellationRequested)
        {
            return VoiceWorkerExitReason.Stopped;
        }

        if (first == heartbeat || first == idle || first == aborted)
        {
            return await ((Task<VoiceWorkerExitReason>)first).ConfigureAwait(false);
        }

        return first == session.Completion ? await session.Completion.ConfigureAwait(false) : VoiceWorkerExitReason.Crashed;
    }

    /// <summary>
    /// Returns <see cref="VoiceWorkerExitReason.ModelIdle"/> once the model has gone unused for
    /// <see cref="VoiceWorkerOptions.ModelIdleTimeout"/> with no request in flight. It stops handing out the session in
    /// the same step, so no request can start on a worker that is about to go.
    /// </summary>
    private async Task<VoiceWorkerExitReason> IdleAsync(VoiceWorkerSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                TimeSpan wait;
                lock (_gate)
                {
                    var idleFor = _requestsInFlight > 0 ? TimeSpan.Zero : _time.GetElapsedTime(_lastUse);
                    wait = _options.ModelIdleTimeout - (_modelLoaded ? idleFor : TimeSpan.Zero);
                    if (wait <= TimeSpan.Zero && _session == session)
                    {
                        _session = null;
                        SignalChanged();
                        return VoiceWorkerExitReason.ModelIdle;
                    }
                }

                await Task.Delay(wait > TimeSpan.Zero ? wait : _options.ModelIdleTimeout, _time, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return VoiceWorkerExitReason.Stopped; // the watch ended for another reason; this result is not read
        }
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
            SignalChanged();
        }

        RaiseStateChanged(state);
    }

    private void RaiseStateChanged(VoiceWorkerState state)
    {
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
