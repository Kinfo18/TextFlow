using System.IO.Compression;
using TextFlow.Core.Voice;

namespace TextFlow.Infrastructure.Voice;

/// <summary>Where each voice file comes from; <see cref="Default"/> are the pinned ones in <see cref="VoiceDownloads"/>.</summary>
public sealed record VoiceSources(Uri Model, long ModelSize, string ModelSha256, Uri GpuPack, long GpuPackSize, string GpuPackSha256, long GpuPackUnpackedSize)
{
    public static VoiceSources Default { get; } = new(
        VoiceDownloads.ModelSource, VoiceDownloads.ModelSize, VoiceDownloads.ModelSha256,
        VoiceDownloads.GpuPackSource, VoiceDownloads.GpuPackSize, VoiceDownloads.GpuPackSha256, VoiceDownloads.GpuPackUnpackedSize);
}

public enum VoiceInstallStep
{
    Model,
    GpuPack,
    Unpacking,
    Done,
}

/// <param name="Done">Bytes downloaded so far, both files together.</param>
/// <param name="Total">Bytes of both downloads (only what was still missing when the install started).</param>
public readonly record struct VoiceInstallProgress(VoiceInstallStep Step, long Done, long Total);

public sealed class NotEnoughDiskSpaceException : IOException
{
    public NotEnoughDiskSpaceException()
    {
    }

    public NotEnoughDiskSpaceException(long neededBytes, long freeBytes)
        : base($"Not enough disk space: {neededBytes} bytes needed, {freeBytes} free.")
    {
        NeededBytes = neededBytes;
        FreeBytes = freeBytes;
    }

    public NotEnoughDiskSpaceException(string message)
        : base(message)
    {
    }

    public NotEnoughDiskSpaceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public long NeededBytes { get; }

    public long FreeBytes { get; }
}

/// <summary>
/// Gets the model and the GPU pack into the data folder (V0.2 H1.4, ADR-0009 §H0.5): checks the free space first,
/// downloads what is missing (resumable, hash-checked), unpacks the pack into a temporary folder and renames it into
/// place only when every DLL is there. Run again after any failure: it continues where it stopped.
/// Whisper.net picks its runtime once per worker, so a worker already running must be replaced after installing.
/// A new pack version goes to a folder of its own (<see cref="VoiceModelFiles.GpuPackFolder"/>), so it is never
/// mistaken for the old one.
/// </summary>
public sealed class VoiceFilesInstaller
{
    /// <summary>Inside the pack, where Whisper.net looks for its CUDA 12 runtime (ADR-0009 §H1.3).</summary>
    public static readonly string RuntimeFolder = Path.Combine("runtimes", "cuda12", "win-x64");

    public static readonly IReadOnlyList<string> RequiredFiles =
    [
        "whisper.dll", "ggml-whisper.dll", "ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-cuda-whisper.dll",
        "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll",
    ];

    /// <summary>Room left over on top of the files themselves.</summary>
    private const long SpaceMargin = 200L * 1024 * 1024;

    /// <summary>Right after unpacking ~1.1 GB, Defender or the indexer may still hold the files for a moment.</summary>
    private const int FolderAttempts = 10;

    private static readonly TimeSpan FolderRetryDelay = TimeSpan.FromMilliseconds(300);

    private readonly ResumableDownloader _downloader;
    private readonly VoiceModelFiles _files;
    private readonly VoiceSources _sources;
    private readonly Func<string, long> _freeSpace;
    private int _installing;

    public VoiceFilesInstaller(
        ResumableDownloader downloader, VoiceModelFiles files, VoiceSources? sources = null, Func<string, long>? freeSpace = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        if (_files.GpuPackPath is null)
        {
            throw new ArgumentException("The voice files need a GPU pack folder.", nameof(files));
        }

        _sources = sources ?? VoiceSources.Default;
        _freeSpace = freeSpace ?? FreeSpace;
    }

    public bool ModelInstalled => File.Exists(_files.ModelPath);

    public bool GpuPackInstalled => IsCompletePack(PackPath);

    public bool Installed => ModelInstalled && GpuPackInstalled;

    private string PackPath => _files.GpuPackPath!;

    private string PackZip => PackPath + ".zip";

    /// <exception cref="NotEnoughDiskSpaceException">Checked before downloading anything.</exception>
    /// <exception cref="DownloadException">A download failed (network, hash, disk); the next call resumes it.</exception>
    /// <exception cref="InvalidDataException">The pack (with the right hash) lacks a DLL: a broken build of it.</exception>
    /// <exception cref="IOException">The unpacked pack could not be moved into place; the next call unpacks it again.</exception>
    /// <exception cref="InvalidOperationException">Another install of this installer is still running.</exception>
    public async Task InstallAsync(IProgress<VoiceInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _installing, 1) == 1)
        {
            throw new InvalidOperationException("The voice files are already being installed.");
        }

        try
        {
            await InstallOnceAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _installing, 0);
        }
    }

    private async Task InstallOnceAsync(IProgress<VoiceInstallProgress>? progress, CancellationToken cancellationToken)
    {
        var model = new DownloadItem(_sources.Model, _sources.ModelSize, _sources.ModelSha256, _files.ModelPath);
        var pack = new DownloadItem(_sources.GpuPack, _sources.GpuPackSize, _sources.GpuPackSha256, PackZip);
        var needPack = !GpuPackInstalled;
        var modelLeft = ModelInstalled ? 0 : Math.Max(0, model.Size - PartLength(model));
        var packLeft = needPack && !File.Exists(PackZip) ? Math.Max(0, pack.Size - PartLength(pack)) : 0;
        CheckSpace(modelLeft + packLeft + (needPack ? _sources.GpuPackUnpackedSize : 0));

        var total = modelLeft + packLeft;
        long modelDone = 0;
        var modelStart = model.Size - modelLeft;
        await _downloader.DownloadAsync(
            model,
            progress is null ? null : new Inline<long>(bytes =>
            {
                modelDone = Math.Max(0, bytes - modelStart);
                progress.Report(new VoiceInstallProgress(VoiceInstallStep.Model, modelDone, total));
            }),
            cancellationToken).ConfigureAwait(false);

        if (needPack)
        {
            var packStart = pack.Size - packLeft;
            await _downloader.DownloadAsync(
                pack,
                progress is null ? null : new Inline<long>(bytes =>
                    progress.Report(new VoiceInstallProgress(VoiceInstallStep.GpuPack, modelDone + Math.Max(0, bytes - packStart), total))),
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new VoiceInstallProgress(VoiceInstallStep.Unpacking, total, total));
            await UnpackAsync(cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new VoiceInstallProgress(VoiceInstallStep.Done, total, total));
    }

    /// <summary>
    /// Removes everything voice put in the data folder (Diagnóstico → "Borrar archivos de voz"). Stop the voice worker
    /// first: it holds the model and the DLL open. The pack folder goes first, so a half-done delete never leaves
    /// something that looks installed.
    /// </summary>
    /// <exception cref="IOException">A file stayed in use; calling it again finishes the job.</exception>
    public async Task DeleteAsync()
    {
        foreach (var folder in new[] { PackPath, PackPath + ".partial" })
        {
            await RetryAsync(() =>
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }).ConfigureAwait(false);
        }

        foreach (var file in new[] { _files.ModelPath, _files.ModelPath + ".part", PackZip, PackZip + ".part" })
        {
            await RetryAsync(() => File.Delete(file)).ConfigureAwait(false);
        }
    }

    private static bool IsCompletePack(string folder) =>
        RequiredFiles.All(name => File.Exists(Path.Combine(folder, RuntimeFolder, name)));

    private static long PartLength(DownloadItem item)
    {
        var part = ResumableDownloader.PartPath(item);
        return File.Exists(part) ? new FileInfo(part).Length : 0;
    }

    private static long FreeSpace(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    private void CheckSpace(long needed)
    {
        if (needed <= 0)
        {
            return;
        }

        var free = _freeSpace(Path.GetDirectoryName(Path.GetFullPath(_files.ModelPath))!);
        if (free < needed + SpaceMargin)
        {
            throw new NotEnoughDiskSpaceException(needed + SpaceMargin, free);
        }
    }

    /// <summary>Into <c>.partial</c> first: a folder named like the pack is always complete.</summary>
    private async Task UnpackAsync(CancellationToken cancellationToken)
    {
        var partial = PackPath + ".partial";
        await RetryAsync(() =>
        {
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }
        }).ConfigureAwait(false);

        // Refuses entries that point outside the folder; the zip's hash is pinned anyway.
        await Task.Run(() => ZipFile.ExtractToDirectory(PackZip, partial), cancellationToken).ConfigureAwait(false);
        if (!IsCompletePack(partial))
        {
            await RetryAsync(() => Directory.Delete(partial, recursive: true)).ConfigureAwait(false);
            File.Delete(PackZip);
            throw new InvalidDataException("The GPU pack lacks some DLL.");
        }

        await RetryAsync(() =>
        {
            if (Directory.Exists(PackPath))
            {
                Directory.Delete(PackPath, recursive: true); // an incomplete one
            }

            Directory.Move(partial, PackPath);
        }).ConfigureAwait(false);
        File.Delete(PackZip);
    }

    /// <summary>Runs a file-system step, retrying while something else briefly holds the files.</summary>
    private static async Task RetryAsync(Action step)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                step();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < FolderAttempts)
            {
                await Task.Delay(FolderRetryDelay).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException("Voice files are in use.", ex);
            }
        }
    }

    /// <summary>Reports on the calling thread: <see cref="Progress{T}"/> would post every 1 MB chunk to the UI.</summary>
    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
