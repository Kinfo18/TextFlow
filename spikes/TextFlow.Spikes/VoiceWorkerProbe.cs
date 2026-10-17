using System.Diagnostics;
using System.Globalization;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Voice;
using TextFlow.Infrastructure.Voice;

namespace TextFlow.Spikes;

/// <summary>
/// V0.2 H1.1 by hand: starts the real voice worker under the supervisor, pings it, kills it <c>rondas</c> times and
/// checks it comes back and answers again. Prints each diagnostic event as it happens. Usage: <c>voice-worker [rondas]</c>.
/// </summary>
internal static class VoiceWorkerProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var rounds = args.Length > 0 && int.TryParse(args[0], CultureInfo.InvariantCulture, out var r) ? r : 3;
        using var launcher = new NamedPipeVoiceWorkerLauncher();
        var sink = new ConsoleSink();
        await using var supervisor = new VoiceWorkerSupervisor(launcher, sink);
        supervisor.StateChanged += state => Console.WriteLine($"  estado: {state}");

        supervisor.Start();
        for (var round = 1; round <= rounds; round++)
        {
            var pid = await WaitReadyAsync(supervisor, sink, round);
            if (pid is null)
            {
                Console.WriteLine($"FALLO: el worker {round} no arrancó (estado {supervisor.State}).");
                return 1;
            }

            var rtt = await supervisor.PingAsync(CancellationToken.None);
            Console.WriteLine($"ronda {round}: worker pid={pid} ping={rtt.TotalMilliseconds:0.0} ms → lo mato");
            using var worker = Process.GetProcessById(pid.Value);
            worker.Kill();
        }

        var last = await WaitReadyAsync(supervisor, sink, rounds + 1);
        if (last is null)
        {
            Console.WriteLine("FALLO: no volvió tras la última muerte.");
            return 1;
        }

        var final = await supervisor.PingAsync(CancellationToken.None);
        Console.WriteLine($"OK: {rounds} muertes, {rounds} reinicios, último ping {final.TotalMilliseconds:0.0} ms");
        return 0;
    }

    private static async Task<int?> WaitReadyAsync(VoiceWorkerSupervisor supervisor, ConsoleSink sink, int startNumber)
    {
        for (var i = 0; i < 300; i++)
        {
            if (sink.Starts.Length >= startNumber && supervisor.State == VoiceWorkerState.Ready)
            {
                return sink.Starts[startNumber - 1];
            }

            await Task.Delay(50);
        }

        return null;
    }

    private sealed class ConsoleSink : IDiagnosticSink
    {
        private readonly Lock _gate = new();
        private readonly List<int> _starts = [];

        public int[] Starts
        {
            get
            {
                lock (_gate)
                {
                    return _starts.ToArray();
                }
            }
        }

        public void Record(DiagnosticEvent diagnostic)
        {
            Console.WriteLine($"  {diagnostic}");
            if (diagnostic is VoiceWorkerStarted started)
            {
                lock (_gate)
                {
                    _starts.Add(started.ProcessId);
                }
            }
        }
    }
}
