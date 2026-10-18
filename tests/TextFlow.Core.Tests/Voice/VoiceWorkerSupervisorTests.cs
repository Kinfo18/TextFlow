using Microsoft.Extensions.Time.Testing;
using TextFlow.Contracts.Voice;
using TextFlow.Core.Tests.Engine;
using TextFlow.Core.Voice;

namespace TextFlow.Core.Tests.Voice;

public sealed class VoiceWorkerSupervisorTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeLauncher _launcher = new();
    private readonly ListSink _sink = new();
    private readonly VoiceWorkerSupervisor _supervisor;

    public VoiceWorkerSupervisorTests()
    {
        _supervisor = new VoiceWorkerSupervisor(_launcher, _sink, _time);
    }

    public async ValueTask DisposeAsync() => await _supervisor.DisposeAsync();

    [Fact]
    public async Task Start_LaunchesTheWorker_AndAnswersPings()
    {
        _supervisor.Start();
        await ReadyAsync(launches: 1);

        await _supervisor.PingAsync(CancellationToken.None);

        var started = Assert.Single(_sink.Events.OfType<VoiceWorkerStarted>());
        Assert.Equal(1001, started.ProcessId);
        Assert.Equal(1, _launcher.Last.PingsAnswered);
    }

    [Fact]
    public async Task Ping_BeforeStart_IsUnavailable()
    {
        await Assert.ThrowsAsync<VoiceWorkerUnavailableException>(() => _supervisor.PingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WorkerCrash_IsLogged_AndTheWorkerIsRestarted()
    {
        _supervisor.Start();
        await ReadyAsync(launches: 1);

        _launcher.Last.Crash(exitCode: 3);
        await Eventually.TrueAsync(() => _supervisor.State == VoiceWorkerState.Restarting, "it waits to restart");
        await AdvanceUntilAsync(() => _launcher.Workers.Count == 2, "it restarts");
        await ReadyAsync(launches: 2);

        var exited = Assert.Single(_sink.Events.OfType<VoiceWorkerExited>());
        Assert.Equal(VoiceWorkerExitReason.Crashed, exited.Reason);
        Assert.Equal(3, exited.ExitCode);
        await _supervisor.PingAsync(CancellationToken.None); // the next request works
    }

    [Fact]
    public async Task PingInFlight_WhenTheWorkerDies_FailsInsteadOfHanging()
    {
        _launcher.DefaultScript = new WorkerScript(AnswerPings: false);
        _supervisor.Start();
        await ReadyAsync(launches: 1);

        var ping = _supervisor.PingAsync(CancellationToken.None);
        _launcher.Last.Crash();

        await Assert.ThrowsAsync<VoiceWorkerUnavailableException>(() => ping);
    }

    [Fact]
    public async Task HungWorker_IsKilledAfterTheHeartbeatTimeout_AndRestarted()
    {
        _launcher.DefaultScript = new WorkerScript(AnswerPings: false);
        _supervisor.Start();
        await ReadyAsync(launches: 1);
        var hung = _launcher.Last;

        await AdvanceUntilAsync(() => hung.Killed, "the hung worker is killed");

        await Eventually.TrueAsync(() => _sink.Events.OfType<VoiceWorkerExited>().Any(), "the exit is logged");
        Assert.Equal(VoiceWorkerExitReason.Hung, _sink.Events.OfType<VoiceWorkerExited>().Single().Reason);
        await AdvanceUntilAsync(() => _launcher.Workers.Count == 2, "it restarts");
    }

    [Fact]
    public async Task HealthyWorker_SurvivesManyHeartbeats()
    {
        _supervisor.Start();
        await ReadyAsync(launches: 1);

        await AdvanceUntilAsync(() => _launcher.Last.PingsAnswered >= 5, "five heartbeats pass");

        Assert.Single(_launcher.Workers);
        Assert.Empty(_sink.Events.OfType<VoiceWorkerExited>());
    }

    [Fact]
    public async Task RestartDelay_DoublesWithEachCrash()
    {
        _supervisor.Start();
        await ReadyAsync(launches: 1);

        // 1st crash: 0.5 s; 2nd: 1 s. Just before each delay ends there is still no new worker.
        _launcher.Last.Crash();
        await Eventually.TrueAsync(() => _supervisor.State == VoiceWorkerState.Restarting, "first wait");
        _time.Advance(TimeSpan.FromMilliseconds(499));
        await Task.Delay(50);
        Assert.Single(_launcher.Workers);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await ReadyAsync(launches: 2);

        _launcher.Last.Crash();
        await Eventually.TrueAsync(() => _supervisor.State == VoiceWorkerState.Restarting, "second wait");
        _time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(50);
        Assert.Equal(2, _launcher.Workers.Count);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await ReadyAsync(launches: 3);
    }

    [Fact]
    public async Task FiveCrashesInARow_Fault_UntilStartedAgain()
    {
        _launcher.FailNext(5);
        _supervisor.Start();

        await AdvanceUntilAsync(() => _supervisor.State == VoiceWorkerState.Faulted, "it gives up");
        Assert.Equal(5, _launcher.Attempts);
        Assert.Equal(5, _sink.Events.OfType<VoiceWorkerExited>().Count(e => e.Reason == VoiceWorkerExitReason.LaunchFailed));
        Assert.Equal(5, Assert.Single(_sink.Events.OfType<VoiceWorkerFaulted>()).Crashes);

        _time.Advance(TimeSpan.FromMinutes(10)); // no more tries on its own
        await Task.Delay(50);
        Assert.Equal(5, _launcher.Attempts);

        _supervisor.Start();
        await ReadyAsync(launches: 1);
    }

    [Fact]
    public async Task CrashesSpreadOverTime_DoNotFault()
    {
        _supervisor.Start();
        await ReadyAsync(launches: 1);

        for (var i = 2; i <= 7; i++)
        {
            _launcher.Last.Crash();
            await Eventually.TrueAsync(() => _supervisor.State == VoiceWorkerState.Restarting, $"crash {i - 1} seen");
            _time.Advance(TimeSpan.FromMinutes(3)); // outside the crash window
            await ReadyAsync(launches: i);
        }

        Assert.Empty(_sink.Events.OfType<VoiceWorkerFaulted>());
    }

    [Fact]
    public async Task OtherProtocolVersion_IsAHandshakeFailure()
    {
        _launcher.Enqueue(new WorkerScript(ProtocolVersion: VoiceProtocol.Version + 1));
        _supervisor.Start();

        await Eventually.TrueAsync(() => _sink.Events.OfType<VoiceWorkerExited>().Any(), "the handshake fails");

        Assert.Equal(VoiceWorkerExitReason.HandshakeFailed, _sink.Events.OfType<VoiceWorkerExited>().Single().Reason);
        Assert.True(_launcher.Last.Killed);
        await AdvanceUntilAsync(() => _launcher.Workers.Count == 2, "it tries again");
        await ReadyAsync(launches: 2);
    }

    [Fact]
    public async Task SilentWorker_FailsTheHandshake_AfterTheTimeout()
    {
        _launcher.Enqueue(new WorkerScript(AnswerHello: false));
        _supervisor.Start();
        await Eventually.TrueAsync(() => _launcher.Workers.Count == 1, "it launches");

        await AdvanceUntilAsync(() => _sink.Events.OfType<VoiceWorkerExited>().Any(), "the handshake times out");

        Assert.Equal(VoiceWorkerExitReason.HandshakeFailed, _sink.Events.OfType<VoiceWorkerExited>().Single().Reason);
    }

    [Fact]
    public async Task Stop_AsksTheWorkerToExit_AndDoesNotRestartIt()
    {
        _supervisor.Start();
        await ReadyAsync(launches: 1);

        await _supervisor.StopAsync();

        var worker = _launcher.Last;
        Assert.True(worker.GotShutdown);
        Assert.False(worker.Killed);
        Assert.True(worker.Disposed);
        Assert.Equal(VoiceWorkerState.Stopped, _supervisor.State);
        var exited = Assert.Single(_sink.Events.OfType<VoiceWorkerExited>());
        Assert.Equal((VoiceWorkerExitReason.Stopped, (int?)0), (exited.Reason, exited.ExitCode));
        await Assert.ThrowsAsync<VoiceWorkerUnavailableException>(() => _supervisor.PingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Stop_KillsAWorkerThatIgnoresShutdown()
    {
        _launcher.DefaultScript = new WorkerScript(ObeyShutdown: false);
        _supervisor.Start();
        await ReadyAsync(launches: 1);

        var stopping = _supervisor.StopAsync();
        await AdvanceUntilAsync(() => stopping.IsCompleted, "stop completes");

        Assert.True(_launcher.Last.Killed);
    }

    [Fact]
    public async Task Stop_WhileFaulted_Completes()
    {
        _launcher.FailNext(5);
        _supervisor.Start();
        await AdvanceUntilAsync(() => _supervisor.State == VoiceWorkerState.Faulted, "it gives up");

        await _supervisor.StopAsync();

        Assert.Equal(VoiceWorkerState.Stopped, _supervisor.State);
    }

    [Fact]
    public async Task StateChanged_ReportsTheLifecycle()
    {
        var states = new List<VoiceWorkerState>();
        _supervisor.StateChanged += state =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };

        _supervisor.Start();
        await ReadyAsync(launches: 1);
        await _supervisor.StopAsync();

        lock (states)
        {
            Assert.Equal([VoiceWorkerState.Starting, VoiceWorkerState.Ready, VoiceWorkerState.Stopped], states);
        }
    }

    [Fact]
    public async Task StartDuringStop_WaitsForTheOldWorker_ThenRunsOneNewWorker()
    {
        _launcher.DefaultScript = new WorkerScript(ObeyShutdown: false); // stopping takes until the kill timeout
        _supervisor.Start();
        await ReadyAsync(launches: 1);
        _launcher.DefaultScript = new WorkerScript();

        var stopping = _supervisor.StopAsync();
        await Eventually.TrueAsync(() => _launcher.Workers[0].GotShutdown, "the old worker is being stopped");
        _supervisor.Start();
        Assert.Single(_launcher.Workers); // not while the old one is still alive

        await AdvanceUntilAsync(() => stopping.IsCompleted, "stop completes");
        await ReadyAsync(launches: 2);
        Assert.True(_launcher.Workers[0].Killed);
        Assert.Equal(VoiceWorkerState.Ready, _supervisor.State); // the finished stop did not overwrite it
    }

    [Fact]
    public async Task Start_AfterStop_LaunchesAgain()
    {
        _supervisor.Start();
        await ReadyAsync(launches: 1);
        await _supervisor.StopAsync();

        _supervisor.Start();

        await ReadyAsync(launches: 2);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotStopTheSupervisor()
    {
        _supervisor.StateChanged += _ => throw new InvalidOperationException("bad subscriber");

        _supervisor.Start();

        await ReadyAsync(launches: 1);
        await _supervisor.PingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CaptureRequests_RoundTripToTheWorker()
    {
        _supervisor.Start();
        await ReadyAsync(launches: 1);

        var microphones = await _supervisor.ListMicrophonesAsync(CancellationToken.None);
        var name = await _supervisor.StartCaptureAsync(null, CancellationToken.None);
        var stopped = await _supervisor.StopCaptureAsync(CancellationToken.None);

        Assert.Equal("Fake mic", Assert.Single(microphones).Name);
        Assert.Equal("Fake mic", name);
        Assert.Equal(1000, stopped.DurationMs);
    }

    [Fact]
    public async Task WorkerErrorReply_IsARequestException_WithItsCode()
    {
        _launcher.DefaultScript = new WorkerScript(CaptureError: VoiceErrorCode.MicrophoneAccessDenied);
        _supervisor.Start();
        await ReadyAsync(launches: 1);

        var error = await Assert.ThrowsAsync<VoiceRequestException>(() => _supervisor.StartCaptureAsync(null, CancellationToken.None));

        Assert.Equal(VoiceErrorCode.MicrophoneAccessDenied, error.Code);
        await _supervisor.PingAsync(CancellationToken.None); // a refused request does not end the worker
    }

    [Fact]
    public async Task CaptureRequest_BeforeStart_IsUnavailable()
    {
        await Assert.ThrowsAsync<VoiceWorkerUnavailableException>(() => _supervisor.StartCaptureAsync(null, CancellationToken.None));
    }

    private async Task ReadyAsync(int launches)
    {
        await Eventually.TrueAsync(
            () => _launcher.Workers.Count == launches && _supervisor.State == VoiceWorkerState.Ready,
            $"worker {launches} is ready");
    }

    /// <summary>Moves the fake clock in small steps, letting the supervisor react between them.</summary>
    private async Task AdvanceUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 2000 && !condition(); i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(2);
        }

        Assert.True(condition(), $"Never happened: {because}.");
    }
}
