using Microsoft.Extensions.Time.Testing;
using TextFlow.Contracts.Voice;
using TextFlow.Core.Tests.Engine;
using TextFlow.Core.Voice;

namespace TextFlow.Core.Tests.Voice;

/// <summary>Loading the speech model on demand and freeing it after inactivity (V0.2 H1.3).</summary>
public sealed class VoiceModelLifecycleTests : IAsyncDisposable
{
    private static readonly VoiceModelFiles Files = new(@"C:\data\models\turbo.bin", @"C:\data\voz\cuda12-1");
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeLauncher _launcher = new();
    private readonly ListSink _sink = new();
    private readonly VoiceWorkerSupervisor _supervisor;

    public VoiceModelLifecycleTests()
    {
        // A short idle time keeps the fake clock walks small (the production default is 15 min). No heartbeat: over
        // long fake-clock walks a pong a few real ms late would count as a hang and restart the worker.
        _supervisor = new VoiceWorkerSupervisor(_launcher, _sink, _time, new VoiceWorkerOptions
        {
            ModelIdleTimeout = IdleTimeout,
            ModelLoadTimeout = TimeSpan.FromSeconds(20),
            HeartbeatInterval = TimeSpan.FromDays(1),
        });
    }

    public async ValueTask DisposeAsync() => await _supervisor.DisposeAsync();

    [Fact]
    public void DefaultIdleTimeout_IsFifteenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), new VoiceWorkerOptions().ModelIdleTimeout);
    }

    [Fact]
    public void ModelFiles_LiveInTheDataFolder()
    {
        var files = VoiceModelFiles.In(@"C:\Users\u\AppData\Local\TextFlow");

        Assert.Equal(@"C:\Users\u\AppData\Local\TextFlow\models\ggml-large-v3-turbo-q5_0.bin", files.ModelPath);
        Assert.Equal(@"C:\Users\u\AppData\Local\TextFlow\voz\cuda12-1", files.GpuPackPath);
    }

    [Fact]
    public async Task EnsureModel_LoadsOnce_ThenFindsItLoaded()
    {
        await StartAsync();

        var first = await _supervisor.EnsureModelAsync(Files, CancellationToken.None);
        var second = await _supervisor.EnsureModelAsync(Files, CancellationToken.None);

        Assert.False(first.WasLoaded);
        Assert.True(second.WasLoaded);
        Assert.Equal(1, _launcher.Last.ModelLoads);
        var loaded = Assert.Single(_sink.Events.OfType<VoiceModelLoaded>());
        Assert.Equal(("Cuda12", 700.0, 400.0), (loaded.Runtime, loaded.LoadMs, loaded.WarmupMs));
    }

    [Fact]
    public async Task IdleModel_IsFreedByAFreshWorker_WithoutCountingACrash()
    {
        await StartAsync();
        await _supervisor.EnsureModelAsync(Files, CancellationToken.None);
        var first = _launcher.Last;

        await AdvanceUntilAsync(() => _launcher.Workers.Count == 2, "the idle worker is replaced");
        await ReadyAsync(launches: 2);

        Assert.True(first.GotShutdown);
        Assert.False(first.Killed);
        Assert.False(_launcher.Last.ModelLoaded);
        var exited = Assert.Single(_sink.Events.OfType<VoiceWorkerExited>());
        Assert.Equal(VoiceWorkerExitReason.ModelIdle, exited.Reason);
    }

    [Fact]
    public async Task IdleRelease_HappensAfterTheTimeout_NotBefore()
    {
        await StartAsync();
        await _supervisor.EnsureModelAsync(Files, CancellationToken.None);

        await AdvanceAsync(IdleTimeout - TimeSpan.FromSeconds(1));
        Assert.Single(_launcher.Workers);

        await AdvanceUntilAsync(() => _launcher.Workers.Count == 2, "it is released just after the timeout");
    }

    [Fact]
    public async Task EachUse_RestartsTheIdleTime()
    {
        await StartAsync();
        await _supervisor.EnsureModelAsync(Files, CancellationToken.None);

        await AdvanceAsync(TimeSpan.FromSeconds(40));
        await _supervisor.StartCaptureAsync(null, CancellationToken.None); // any request is a use
        await AdvanceAsync(TimeSpan.FromSeconds(40));

        Assert.Single(_launcher.Workers); // 80 s after the load, but only 40 s after the last use
        await AdvanceUntilAsync(() => _launcher.Workers.Count == 2, "released 60 s after the last use");
    }

    [Fact]
    public async Task WorkerWithoutModel_IsNeverReplaced()
    {
        await StartAsync();

        await AdvanceAsync(IdleTimeout * 3);

        Assert.Single(_launcher.Workers);
        Assert.Empty(_sink.Events.OfType<VoiceWorkerExited>());
    }

    [Fact]
    public async Task ModelAfterARelease_LoadsAgainTransparently()
    {
        await StartAsync();
        await _supervisor.EnsureModelAsync(Files, CancellationToken.None);
        await AdvanceUntilAsync(() => _launcher.Workers.Count == 2, "released");
        await ReadyAsync(launches: 2);

        var reply = await _supervisor.EnsureModelAsync(Files, CancellationToken.None);

        Assert.False(reply.WasLoaded);
        Assert.Equal(1, _launcher.Last.ModelLoads);
    }

    [Fact]
    public async Task RequestDuringTheRelease_WaitsForTheFreshWorker()
    {
        _launcher.DefaultScript = new WorkerScript(ObeyShutdown: false); // the release takes until the kill timeout
        await StartAsync();
        await _supervisor.EnsureModelAsync(Files, CancellationToken.None);
        _launcher.DefaultScript = new WorkerScript();

        await AdvanceUntilAsync(() => _launcher.Workers[0].GotShutdown, "the release begins");
        var ensure = _supervisor.EnsureModelAsync(Files, CancellationToken.None);
        await AdvanceUntilAsync(() => ensure.IsCompleted, "the request is served");

        Assert.False((await ensure).WasLoaded);
        Assert.Equal(2, _launcher.Workers.Count);
        Assert.Equal(1, _launcher.Workers[1].ModelLoads);
    }

    [Fact]
    public async Task ManyReleases_NeverFault()
    {
        await StartAsync();

        for (var launches = 2; launches <= 7; launches++)
        {
            await _supervisor.EnsureModelAsync(Files, CancellationToken.None);
            await AdvanceUntilAsync(() => _launcher.Workers.Count == launches, $"release {launches - 1}");
            await ReadyAsync(launches);
        }

        Assert.Empty(_sink.Events.OfType<VoiceWorkerFaulted>());
        Assert.All(_sink.Events.OfType<VoiceWorkerExited>(), e => Assert.Equal(VoiceWorkerExitReason.ModelIdle, e.Reason));
    }

    [Fact]
    public async Task ModelError_IsARequestException_AndLogged_AndTheWorkerStays()
    {
        _launcher.DefaultScript = new WorkerScript(ModelError: VoiceErrorCode.NoGpu);
        await StartAsync();

        var error = await Assert.ThrowsAsync<VoiceRequestException>(() => _supervisor.EnsureModelAsync(Files, CancellationToken.None));

        Assert.Equal(VoiceErrorCode.NoGpu, error.Code);
        var failed = Assert.Single(_sink.Events.OfType<VoiceModelLoadFailed>());
        Assert.Equal((VoiceErrorCode.NoGpu, false), (failed.Code, failed.TimedOut));
        await _supervisor.PingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HungLoad_TimesOut_AndTheWorkerIsKilledAndRestarted()
    {
        _launcher.Enqueue(new WorkerScript(AnswerModelLoad: false));
        await StartAsync();
        var hung = _launcher.Last;

        var ensure = _supervisor.EnsureModelAsync(Files, CancellationToken.None);
        await AdvanceUntilAsync(() => ensure.IsCompleted, "the load times out");

        await Assert.ThrowsAsync<TimeoutException>(() => ensure);
        Assert.True(Assert.Single(_sink.Events.OfType<VoiceModelLoadFailed>()).TimedOut);
        await Eventually.TrueAsync(() => hung.Killed, "the hung worker is killed");
        Assert.Equal(VoiceWorkerExitReason.Hung, Assert.Single(_sink.Events.OfType<VoiceWorkerExited>()).Reason);
        await AdvanceUntilAsync(() => _launcher.Workers.Count == 2, "it restarts");
    }

    [Fact]
    public async Task CancelledLoad_IsStillReleasedWhenIdle()
    {
        _launcher.Enqueue(new WorkerScript(AnswerModelLoad: false)); // the load is still running when the caller gives up
        await StartAsync();
        using var cancel = new CancellationTokenSource();

        var ensure = _supervisor.EnsureModelAsync(Files, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ensure);

        await AdvanceUntilAsync(() => _launcher.Workers.Count == 2, "the worker that may hold the model is replaced");
        Assert.Equal(VoiceWorkerExitReason.ModelIdle, Assert.Single(_sink.Events.OfType<VoiceWorkerExited>()).Reason);
    }

    [Fact]
    public async Task EnsureModel_RightAfterLeavingFaulted_WaitsForTheWorker()
    {
        _launcher.FailNext(5);
        _supervisor.Start();
        await AdvanceUntilAsync(() => _supervisor.State == VoiceWorkerState.Faulted, "it gives up");

        _supervisor.Start();
        var reply = await _supervisor.EnsureModelAsync(Files, CancellationToken.None);

        Assert.False(reply.WasLoaded);
    }

    [Fact]
    public async Task EnsureModel_BeforeStart_IsUnavailable()
    {
        await Assert.ThrowsAsync<VoiceWorkerUnavailableException>(() => _supervisor.EnsureModelAsync(Files, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureModel_RightAfterStart_WaitsForTheWorker()
    {
        _supervisor.Start();

        var reply = await _supervisor.EnsureModelAsync(Files, CancellationToken.None);

        Assert.False(reply.WasLoaded);
    }

    private async Task StartAsync()
    {
        _supervisor.Start();
        await ReadyAsync(launches: 1);
    }

    private async Task ReadyAsync(int launches)
    {
        await Eventually.TrueAsync(
            () => _launcher.Workers.Count == launches && _supervisor.State == VoiceWorkerState.Ready,
            $"worker {launches} is ready");
    }

    /// <summary>Moves the fake clock by <paramref name="span"/> in 1 s steps, letting the supervisor react between them.</summary>
    private async Task AdvanceAsync(TimeSpan span)
    {
        for (var moved = TimeSpan.Zero; moved < span; moved += TimeSpan.FromSeconds(1))
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2);
        }
    }

    private async Task AdvanceUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 600 && !condition(); i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(2);
        }

        Assert.True(condition(), $"Never happened: {because}.");
    }
}
