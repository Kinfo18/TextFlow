using System.Diagnostics;
using System.Globalization;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Voice;
using TextFlow.Infrastructure.Voice;

namespace TextFlow.Spikes;

/// <summary>
/// V0.2 H1.2 by hand: lists the microphones the worker sees, records <c>segundos</c> from one (default: Windows'
/// default) and prints the captured duration and peak level. The audio never leaves the worker and is not saved.
/// Usage: <c>voice-capture [segundos] [n.º de micrófono] [veces]</c>.
/// </summary>
internal static class VoiceCaptureProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var seconds = args.Length > 0 && double.TryParse(args[0], CultureInfo.InvariantCulture, out var s) ? s : 5;
        var index = args.Length > 1 && int.TryParse(args[1], CultureInfo.InvariantCulture, out var i) ? i : -1;
        var rounds = args.Length > 2 && int.TryParse(args[2], CultureInfo.InvariantCulture, out var r) ? r : 1;

        using var launcher = new NamedPipeVoiceWorkerLauncher();
        await using var supervisor = new VoiceWorkerSupervisor(launcher, new NullSink());
        supervisor.Start();
        for (var wait = 0; supervisor.State != VoiceWorkerState.Ready; wait++)
        {
            if (wait > 200)
            {
                Console.WriteLine($"FALLO: el worker no arrancó (estado {supervisor.State}).");
                return 1;
            }

            await Task.Delay(50);
        }

        var microphones = await supervisor.ListMicrophonesAsync(CancellationToken.None);
        Console.WriteLine($"{microphones.Count} micrófono(s):");
        for (var n = 0; n < microphones.Count; n++)
        {
            Console.WriteLine($"  {n}: {microphones[n].Name}{(microphones[n].IsDefault ? "  (predeterminado)" : "")}");
        }

        var chosen = index >= 0 && index < microphones.Count ? microphones[index].Id : null;
        var allOk = true;
        for (var round = 1; round <= rounds; round++)
        {
            allOk &= await RecordAsync(supervisor, chosen, seconds);
        }

        return allOk ? 0 : 1;
    }

    private static async Task<bool> RecordAsync(VoiceWorkerSupervisor supervisor, string? chosen, double seconds)
    {
        try
        {
            var opening = Stopwatch.StartNew();
            var name = await supervisor.StartCaptureAsync(chosen, CancellationToken.None);
            Console.WriteLine($"Grabando {seconds:0.#} s de «{name}» (abrir: {opening.ElapsedMilliseconds} ms). Habla…");
            var recording = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            var result = await supervisor.StopCaptureAsync(CancellationToken.None);
            var wall = recording.Elapsed.TotalMilliseconds;
            Console.WriteLine(
                $"Capturado {result.DurationMs:0} ms (reloj {wall:0} ms, diferencia {result.DurationMs - wall:+0;-0} ms), " +
                $"pico {result.PeakLevel:0.000}{(result.Truncated ? ", RECORTADO" : "")}");
            var ok = Math.Abs(result.DurationMs - (seconds * 1000)) < 300 && result.PeakLevel > 0.01;
            Console.WriteLine(ok ? "OK" : "REVISAR: duración fuera de ±300 ms o micrófono en silencio");
            return ok;
        }
        catch (VoiceRequestException ex)
        {
            Console.WriteLine($"FALLO: {ex.Code}");
            return false;
        }
    }

    private sealed class NullSink : IDiagnosticSink
    {
        public void Record(DiagnosticEvent diagnostic)
        {
        }
    }
}
