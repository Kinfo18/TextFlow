using System.Diagnostics;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Voice;
using TextFlow.Infrastructure.Voice;
using TextFlow.VoiceWorker;

namespace TextFlow.Infrastructure.Tests.Voice;

/// <summary>V0.2 H1.1 with the real <c>TextFlow.VoiceWorker.exe</c>, a real named pipe and a real job object.</summary>
public sealed class VoiceWorkerProcessTests : IAsyncDisposable
{
    private static readonly string WorkerPath = Path.Combine(AppContext.BaseDirectory, NamedPipeVoiceWorkerLauncher.WorkerFileName);

    private readonly NamedPipeVoiceWorkerLauncher _launcher = new(WorkerPath);
    private readonly Sink _sink = new();
    private readonly VoiceWorkerSupervisor _supervisor;

    public VoiceWorkerProcessTests()
    {
        // Fast restarts so the test does not wait half a second per crash.
        _supervisor = new VoiceWorkerSupervisor(_launcher, _sink, options: new VoiceWorkerOptions { FirstRestartDelay = TimeSpan.FromMilliseconds(50) });
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync();
        _launcher.Dispose();
    }

    [Fact]
    public void WorkerExe_IsNextToTheTests()
    {
        Assert.True(File.Exists(WorkerPath), WorkerPath);
    }

    [Fact]
    public async Task KilledWorker_IsRestarted_AndTheNextRequestWorks()
    {
        _supervisor.Start();
        var first = await WaitForStartAsync(1);
        await _supervisor.PingAsync(CancellationToken.None);

        using (var worker = Process.GetProcessById(first.ProcessId))
        {
            worker.Kill();
        }

        var second = await WaitForStartAsync(2);
        Assert.NotEqual(first.ProcessId, second.ProcessId);
        await _supervisor.PingAsync(CancellationToken.None);
        Assert.Equal(VoiceWorkerExitReason.Crashed, Assert.Single(_sink.Of<VoiceWorkerExited>()).Reason);
    }

    [Fact]
    public async Task Stop_EndsTheWorkerCleanly()
    {
        _supervisor.Start();
        var started = await WaitForStartAsync(1);
        using var worker = Process.GetProcessById(started.ProcessId);

        await _supervisor.StopAsync();

        Assert.True(worker.WaitForExit(5000));
        var exited = Assert.Single(_sink.Of<VoiceWorkerExited>());
        Assert.Equal((VoiceWorkerExitReason.Stopped, (int?)WorkerExitCode.Ok), (exited.Reason, exited.ExitCode));
    }

    [Fact]
    public async Task ClosingTheJob_KillsTheWorker_AsWhenTextFlowDies()
    {
        using var launcher = new NamedPipeVoiceWorkerLauncher(WorkerPath);
        var connection = await launcher.LaunchAsync(CancellationToken.None);
        await using (connection)
        {
            Assert.False(connection.Exited.IsCompleted); // no hello sent: on its own it would wait for one forever

            launcher.Dispose(); // last handle of the kill-on-close job (Windows reports exit code 0 for it)

            await connection.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task MissingWorkerExe_FailsTheLaunch()
    {
        using var launcher = new NamedPipeVoiceWorkerLauncher(Path.Combine(AppContext.BaseDirectory, "no-such-worker.exe"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => launcher.LaunchAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(new[] { "--pipe", "abc", "--parent", "12" }, true)]
    [InlineData(new[] { "--parent", "12", "--pipe", "abc" }, true)]
    [InlineData(new[] { "--pipe", "abc" }, false)]
    [InlineData(new[] { "--pipe", "abc", "--parent", "-1" }, false)]
    [InlineData(new[] { "--pipe", "abc", "--parent", "x" }, false)]
    [InlineData(new[] { "--other", "1", "--pipe", "abc", "--parent", "12" }, false)]
    [InlineData(new string[0], false)]
    public void WorkerArguments_AreStrict(string[] args, bool valid)
    {
        Assert.Equal(valid, WorkerHost.TryParse(args, out _, out _));
    }

    [Fact]
    public async Task WorkerWithBadArguments_ExitsAtOnce()
    {
        using var worker = Process.Start(new ProcessStartInfo(WorkerPath, "--bogus") { UseShellExecute = false, CreateNoWindow = true })!;

        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkerExitCode.BadArguments, worker.ExitCode);
    }

    private async Task<VoiceWorkerStarted> WaitForStartAsync(int count)
    {
        var deadline = Environment.TickCount64 + 15_000;
        while (_sink.Of<VoiceWorkerStarted>().Length < count || _supervisor.State != VoiceWorkerState.Ready)
        {
            Assert.True(Environment.TickCount64 < deadline, $"worker {count} never started; state {_supervisor.State}");
            await Task.Delay(20);
        }

        return _sink.Of<VoiceWorkerStarted>()[count - 1];
    }

    private sealed class Sink : IDiagnosticSink
    {
        private readonly Lock _gate = new();
        private readonly List<DiagnosticEvent> _events = [];

        public void Record(DiagnosticEvent diagnostic)
        {
            lock (_gate)
            {
                _events.Add(diagnostic);
            }
        }

        public T[] Of<T>()
            where T : DiagnosticEvent
        {
            lock (_gate)
            {
                return _events.OfType<T>().ToArray();
            }
        }
    }
}
