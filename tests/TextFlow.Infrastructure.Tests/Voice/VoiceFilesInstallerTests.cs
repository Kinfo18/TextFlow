using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using TextFlow.Core.Voice;
using TextFlow.Infrastructure.Voice;

namespace TextFlow.Infrastructure.Tests.Voice;

/// <summary>Getting the model and the GPU pack into the data folder (V0.2 H1.4).</summary>
public sealed class VoiceFilesInstallerTests : IDisposable
{
    private static readonly byte[] Model = RandomNumberGenerator.GetBytes(2000);

    private readonly string _data = Path.Combine(Path.GetTempPath(), "textflow-voice-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileServer _server = new();
    private readonly HttpClient _http;
    private readonly ResumableDownloader _downloader;
    private readonly VoiceModelFiles _files;
    private VoiceSources _sources;

    public VoiceFilesInstallerTests()
    {
        _http = new HttpClient(_server);
        _downloader = new ResumableDownloader(_http, new DownloadOptions
        {
            FirstRetryDelay = TimeSpan.Zero,
            MaxRetryDelay = TimeSpan.Zero,
            MaxAttemptsWithoutProgress = 2,
        });
        _files = VoiceModelFiles.In(_data);
        _sources = Sources(PackZip(VoiceFilesInstaller.RequiredFiles));
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    [Fact]
    public async Task Install_GetsTheModel_AndUnpacksTheGpuPack()
    {
        var installer = Installer();
        var seen = new List<VoiceInstallProgress>();

        await installer.InstallAsync(new Sync(seen.Add), CancellationToken.None);

        Assert.True(installer.Installed);
        Assert.Equal(Model, await File.ReadAllBytesAsync(_files.ModelPath));
        Assert.True(File.Exists(Path.Combine(_files.GpuPackPath!, VoiceFilesInstaller.RuntimeFolder, "cudart64_12.dll")));
        Assert.False(File.Exists(_files.GpuPackPath + ".zip"));
        Assert.False(Directory.Exists(_files.GpuPackPath + ".partial"));
        Assert.Equal(VoiceInstallStep.Done, seen[^1].Step);
        Assert.Equal(seen[^1].Total, seen[^1].Done);
        Assert.Equal(Model.Length + _sources.GpuPackSize, seen[^1].Total);
    }

    [Fact]
    public async Task NotEnoughSpace_FailsBeforeDownloading()
    {
        var installer = Installer(freeBytes: 1000);

        var error = await Assert.ThrowsAsync<NotEnoughDiskSpaceException>(() => installer.InstallAsync(null, CancellationToken.None));

        Assert.Equal(1000, error.FreeBytes);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task PackWithoutAllTheDll_IsRejected_AndLeavesNoPackFolder()
    {
        _sources = Sources(PackZip(["whisper.dll"]));
        var installer = Installer();

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(null, CancellationToken.None));

        Assert.False(Directory.Exists(_files.GpuPackPath));
        Assert.False(installer.GpuPackInstalled);
        Assert.True(installer.ModelInstalled);
    }

    [Fact]
    public async Task AfterAFailure_TheNextRunOnlyFetchesWhatIsMissing()
    {
        var working = _sources;
        _sources = _sources with { GpuPack = new Uri("https://files.test/missing.zip") };
        var error = await Assert.ThrowsAsync<DownloadException>(() => Installer().InstallAsync(null, CancellationToken.None));
        Assert.Equal(DownloadFailure.Refused, error.Failure);
        _sources = working;
        var before = _server.Requests.Count;

        var installer = Installer();
        await installer.InstallAsync(null, CancellationToken.None);

        Assert.Equal(["pack.zip"], _server.Requests.Skip(before).Select(r => r.Path));
        Assert.True(installer.Installed);
    }

    [Fact]
    public async Task EverythingInstalled_DownloadsNothing()
    {
        var installer = Installer();
        await installer.InstallAsync(null, CancellationToken.None);
        var before = _server.Requests.Count;

        await installer.InstallAsync(null, CancellationToken.None);

        Assert.Equal(before, _server.Requests.Count);
    }

    [Fact]
    public async Task Delete_RemovesAllTheVoiceFiles()
    {
        var installer = Installer();
        await installer.InstallAsync(null, CancellationToken.None);

        await installer.DeleteAsync();

        Assert.False(installer.ModelInstalled);
        Assert.False(Directory.Exists(_files.GpuPackPath));
    }

    [Fact]
    public async Task SecondInstallAtTheSameTime_IsRefused()
    {
        _server.CutAfterBytes = 100;
        _server.StallInsteadOfCut = true; // keeps the first install busy until cancelled
        var installer = Installer();
        using var cancel = new CancellationTokenSource();
        var first = installer.InstallAsync(null, cancel.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(null, CancellationToken.None));

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    private VoiceFilesInstaller Installer(long freeBytes = long.MaxValue) =>
        new(_downloader, _files, _sources, _ => freeBytes);

    private VoiceSources Sources(byte[] pack) => new(
        _server.Add("model.bin", Model), Model.Length, ResumableDownloaderTests.Sha(Model),
        _server.Add("pack.zip", pack), pack.Length, ResumableDownloaderTests.Sha(pack), GpuPackUnpackedSize: 10_000);

    private static byte[] PackZip(IEnumerable<string> dlls)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var dll in dlls)
            {
                using var entry = zip.CreateEntry("runtimes/cuda12/win-x64/" + dll).Open();
                entry.Write(RandomNumberGenerator.GetBytes(64));
            }

            using var notice = zip.CreateEntry("THIRD-PARTY-NOTICES.txt").Open();
            notice.Write("MIT"u8);
        }

        return buffer.ToArray();
    }

    private sealed class Sync(Action<VoiceInstallProgress> report) : IProgress<VoiceInstallProgress>
    {
        public void Report(VoiceInstallProgress value) => report(value);
    }
}
