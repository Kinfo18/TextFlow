using System.Diagnostics;
using System.Net.Http;
using TextFlow.Core.Voice;
using TextFlow.Infrastructure.Voice;

namespace TextFlow.Spikes;

/// <summary>
/// V0.2 H1.4 by hand: downloads the voice files into <c>carpeta</c> (a data folder: models\ and voz\ go under it),
/// printing progress, so it can be killed half way and run again to see it resume. <c>solo-modelo</c> skips the
/// GPU pack. Usage: <c>voice-download carpeta [solo-modelo]</c>.
/// </summary>
internal static class VoiceDownloadProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Uso: voice-download carpeta [solo-modelo]");
            return 2;
        }

        var files = VoiceModelFiles.In(Path.GetFullPath(args[0]));
        var modelOnly = args.Length > 1 && args[1] == "solo-modelo";
        var cuda = NvidiaDriver.CudaVersion();
        Console.WriteLine(cuda is { } v ? $"Driver NVIDIA con CUDA {v / 1000}.{v % 1000 / 10}" : "Sin driver NVIDIA");

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TextFlow-voice-probe");
        var downloader = new ResumableDownloader(http);
        var watch = Stopwatch.StartNew();
        var lastShown = -1L;
        var report = new Inline<long>(bytes =>
        {
            var mb = bytes / (50L * 1024 * 1024);
            if (mb != lastShown)
            {
                lastShown = mb;
                Console.WriteLine($"  {bytes / 1048576.0:0} MiB ({watch.Elapsed.TotalSeconds:0} s)");
            }
        });

        try
        {
            if (modelOnly)
            {
                var part = files.ModelPath + ".part";
                Console.WriteLine(File.Exists(part) ? $"Reanudando desde {new FileInfo(part).Length / 1048576.0:0} MiB" : "Empezando");
                await downloader.DownloadAsync(
                    new DownloadItem(VoiceDownloads.ModelSource, VoiceDownloads.ModelSize, VoiceDownloads.ModelSha256, files.ModelPath),
                    report,
                    CancellationToken.None);
            }
            else
            {
                var installer = new VoiceFilesInstaller(downloader, files);
                await installer.InstallAsync(
                    new Inline<VoiceInstallProgress>(p => report.Report(p.Done)), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is DownloadException or NotEnoughDiskSpaceException or InvalidDataException)
        {
            Console.WriteLine($"FALLO: {(ex is DownloadException d ? d.Failure.ToString() : ex.Message)}");
            return 1;
        }

        Console.WriteLine($"OK en {watch.Elapsed.TotalSeconds:0} s, hash verificado, en {Path.GetDirectoryName(files.ModelPath)}");
        return 0;
    }

    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
