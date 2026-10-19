using System.Diagnostics;
using System.Globalization;
using TextFlow.Core.Diagnostics;
using TextFlow.Core.Voice;
using TextFlow.Infrastructure.Voice;

namespace TextFlow.Spikes;

/// <summary>
/// V0.2 H1.3 by hand: loads the speech model in the worker, asks again (already loaded), waits for the idle release
/// (short timeout here) and checks with nvidia-smi that the VRAM is back to where it started, then loads it again.
/// Usage: <c>voice-model [segundos de inactividad] [modelo.bin] [carpeta del paquete GPU]</c>; without paths, the data
/// folder ones (<c>%LOCALAPPDATA%\TextFlow</c>).
/// </summary>
internal static class VoiceModelProbe
{
    /// <summary>What the desktop and other apps may move the GPU's memory by meanwhile.</summary>
    private const int VramToleranceMiB = 50;

    public static async Task<int> RunAsync(string[] args)
    {
        var idleSeconds = args.Length > 0 && double.TryParse(args[0], CultureInfo.InvariantCulture, out var s) ? s : 20;
        var files = args.Length > 2
            ? new VoiceModelFiles(args[1], args[2])
            : VoiceModelFiles.In(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextFlow"));

        var baseline = GpuMemoryMiB();
        if (baseline is null)
        {
            Console.WriteLine("FALLO: nvidia-smi no responde (¿sin GPU NVIDIA?).");
            return 1;
        }

        Console.WriteLine($"VRAM al empezar: {baseline} MiB. Modelo: {files.ModelPath}");
        var sink = new ExitSink();
        using var launcher = new NamedPipeVoiceWorkerLauncher();
        await using var supervisor = new VoiceWorkerSupervisor(
            launcher, sink, options: new VoiceWorkerOptions { ModelIdleTimeout = TimeSpan.FromSeconds(idleSeconds) });
        supervisor.Start();

        var ok = true;
        try
        {
            var watch = Stopwatch.StartNew();
            var first = await supervisor.EnsureModelAsync(files, CancellationToken.None);
            Console.WriteLine(
                $"Cargado en {watch.ElapsedMilliseconds} ms desde el arranque del worker (runtime {first.Runtime}, " +
                $"carga {first.LoadMs:0} ms, calentamiento {first.WarmupMs:0} ms). VRAM: {GpuMemoryMiB()} MiB");

            watch.Restart();
            var again = await supervisor.EnsureModelAsync(files, CancellationToken.None);
            Console.WriteLine($"Segunda petición: {watch.ElapsedMilliseconds} ms, ya cargado = {again.WasLoaded}");
            ok &= again.WasLoaded;

            Console.WriteLine($"Esperando la liberación por inactividad ({idleSeconds:0.#} s)…");
            watch.Restart();
            while (!sink.ReleasedByIdle)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(idleSeconds + 30))
                {
                    Console.WriteLine("FALLO: no se liberó.");
                    return 1;
                }

                await Task.Delay(200);
            }

            await Task.Delay(1000); // let the driver account the freed memory
            var after = GpuMemoryMiB();
            var back = after is { } a && Math.Abs(a - baseline.Value) <= VramToleranceMiB;
            Console.WriteLine(
                $"Liberado a los {watch.Elapsed.TotalSeconds:0.0} s. VRAM: {after} MiB (base {baseline}) → " +
                (back ? "OK, vuelve a la base" : $"REVISAR: más de ±{VramToleranceMiB} MiB de la base"));
            ok &= back;

            watch.Restart();
            var reload = await supervisor.EnsureModelAsync(files, CancellationToken.None);
            Console.WriteLine(
                $"Recarga transparente: {watch.ElapsedMilliseconds} ms (carga {reload.LoadMs:0} ms, " +
                $"calentamiento {reload.WarmupMs:0} ms). VRAM: {GpuMemoryMiB()} MiB");
            ok &= !reload.WasLoaded;
        }
        catch (VoiceRequestException ex)
        {
            Console.WriteLine($"FALLO: {ex.Code}");
            return 1;
        }

        await supervisor.StopAsync();
        await Task.Delay(1000);
        Console.WriteLine($"Worker parado. VRAM: {GpuMemoryMiB()} MiB");
        Console.WriteLine(ok ? "OK" : "REVISAR");
        return ok ? 0 : 1;
    }

    /// <summary>Memory in use on the whole GPU, as nvidia-smi reports it; null without one.</summary>
    private static int? GpuMemoryMiB()
    {
        try
        {
            using var smi = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (smi is null)
            {
                return null;
            }

            var text = smi.StandardOutput.ReadToEnd().Trim();
            smi.WaitForExit();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) ? mib : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private sealed class ExitSink : IDiagnosticSink
    {
        private volatile bool _released;

        public bool ReleasedByIdle => _released;

        public void Record(DiagnosticEvent diagnostic)
        {
            if (diagnostic is VoiceWorkerExited { Reason: VoiceWorkerExitReason.ModelIdle })
            {
                _released = true;
            }
        }
    }
}
