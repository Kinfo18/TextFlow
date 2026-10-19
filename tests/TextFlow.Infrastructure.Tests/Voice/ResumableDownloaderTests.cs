using System.Net;
using System.Security.Cryptography;
using TextFlow.Infrastructure.Voice;

namespace TextFlow.Infrastructure.Tests.Voice;

/// <summary>Resumable, hash-checked downloads (V0.2 H1.4) against an in-memory server that cuts and stalls.</summary>
public sealed class ResumableDownloaderTests : IDisposable
{
    private static readonly byte[] Content = RandomNumberGenerator.GetBytes(1000);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "textflow-download-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFileServer _server = new();
    private readonly HttpClient _http;
    private readonly ResumableDownloader _downloader;
    private readonly DownloadItem _item;

    public ResumableDownloaderTests()
    {
        _http = new HttpClient(_server);
        _downloader = Downloader(stallTimeout: TimeSpan.FromSeconds(10));
        _item = new DownloadItem(_server.Add("model.bin", Content), Content.Length, Sha(Content), Path.Combine(_folder, "model.bin"));
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task WholeFile_IsCheckedAndRenamedIntoPlace()
    {
        await _downloader.DownloadAsync(_item, null, CancellationToken.None);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_item.Destination));
        Assert.False(File.Exists(ResumableDownloader.PartPath(_item)));
    }

    [Fact]
    public async Task CutConnections_AreResumedWithRange()
    {
        _server.CutAfterBytes = 300;

        await _downloader.DownloadAsync(_item, null, CancellationToken.None);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_item.Destination));
        Assert.Equal([null, 300L, 600L, 900L], _server.Requests.Select(r => r.From));
    }

    [Fact]
    public async Task PartLeftByAnEarlierRun_IsResumed()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(ResumableDownloader.PartPath(_item), Content[..400]);

        await _downloader.DownloadAsync(_item, null, CancellationToken.None);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_item.Destination));
        Assert.Equal(400L, Assert.Single(_server.Requests).From);
    }

    [Fact]
    public async Task ServerIgnoringRange_StartsOverFromZero()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(ResumableDownloader.PartPath(_item), Content[..400]);
        _server.IgnoreRange = true;

        await _downloader.DownloadAsync(_item, null, CancellationToken.None);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_item.Destination));
    }

    [Fact]
    public async Task WrongHash_DeletesTheFile_AndSaysSo()
    {
        var item = _item with { Sha256 = Sha([1, 2, 3]) };

        var error = await Assert.ThrowsAsync<DownloadException>(() => _downloader.DownloadAsync(item, null, CancellationToken.None));

        Assert.Equal(DownloadFailure.HashMismatch, error.Failure);
        Assert.False(File.Exists(item.Destination));
        Assert.False(File.Exists(ResumableDownloader.PartPath(item)));
    }

    [Fact]
    public async Task ServerErrors_AreRetried()
    {
        _server.Statuses.Enqueue(HttpStatusCode.ServiceUnavailable);
        _server.Statuses.Enqueue(HttpStatusCode.TooManyRequests);

        await _downloader.DownloadAsync(_item, null, CancellationToken.None);

        Assert.Equal(3, _server.Requests.Count);
        Assert.True(File.Exists(_item.Destination));
    }

    [Fact]
    public async Task NotFound_IsRefusedAtOnce()
    {
        _server.Statuses.Enqueue(HttpStatusCode.NotFound);

        var error = await Assert.ThrowsAsync<DownloadException>(() => _downloader.DownloadAsync(_item, null, CancellationToken.None));

        Assert.Equal(DownloadFailure.Refused, error.Failure);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task NoProgress_GivesUp_AndKeepsWhatArrived()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(ResumableDownloader.PartPath(_item), Content[..400]);
        _server.CutAfterBytes = 0;

        var error = await Assert.ThrowsAsync<DownloadException>(() => _downloader.DownloadAsync(_item, null, CancellationToken.None));

        Assert.Equal(DownloadFailure.NetworkGaveUp, error.Failure);
        Assert.Equal(3, _server.Requests.Count);
        Assert.Equal(400, new FileInfo(ResumableDownloader.PartPath(_item)).Length);
    }

    [Fact]
    public async Task StalledConnection_IsDroppedAndResumed()
    {
        _server.CutAfterBytes = 500;
        _server.StallInsteadOfCut = true;

        await Downloader(stallTimeout: TimeSpan.FromMilliseconds(200)).DownloadAsync(_item, null, CancellationToken.None);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_item.Destination));
        Assert.Equal([null, 500L], _server.Requests.Select(r => r.From));
    }

    [Fact]
    public async Task OtherFileInPlace_IsReplacedByTheRightOne()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(_item.Destination, Content[..1000].Reverse().ToArray());

        await _downloader.DownloadAsync(_item, null, CancellationToken.None);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_item.Destination));
    }

    [Fact]
    public async Task UnexpectedRange_KeepsThePart_AndAsksAgain()
    {
        _server.CutAfterBytes = 400;
        _server.ShiftRangeOnce = true;

        await _downloader.DownloadAsync(_item, null, CancellationToken.None);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_item.Destination));
        Assert.Equal([null, 400L, 400L, 800L], _server.Requests.Select(r => r.From));
    }

    [Fact]
    public async Task PartLongerThanTheServerFile_StartsOver()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(ResumableDownloader.PartPath(_item), RandomNumberGenerator.GetBytes(1500));

        await _downloader.DownloadAsync(_item, null, CancellationToken.None);

        Assert.Equal(Content, await File.ReadAllBytesAsync(_item.Destination));
        Assert.Null(Assert.Single(_server.Requests).From);
    }

    [Fact]
    public async Task DiskFailure_IsADiskError_NotANetworkOne()
    {
        Directory.CreateDirectory(ResumableDownloader.PartPath(_item)); // a folder where the .part should go

        var error = await Assert.ThrowsAsync<DownloadException>(() => _downloader.DownloadAsync(_item, null, CancellationToken.None));

        Assert.Equal(DownloadFailure.Disk, error.Failure);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task FileAlreadyInPlace_IsNotDownloadedAgain()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(_item.Destination, Content);

        await _downloader.DownloadAsync(_item, null, CancellationToken.None);

        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task OtherSizeOnTheServer_IsASizeMismatch()
    {
        var item = _item with { Size = 600 };

        var error = await Assert.ThrowsAsync<DownloadException>(() => _downloader.DownloadAsync(item, null, CancellationToken.None));

        Assert.Equal(DownloadFailure.SizeMismatch, error.Failure);
        Assert.False(File.Exists(ResumableDownloader.PartPath(item)));
    }

    [Fact]
    public async Task Cancel_StopsAndKeepsThePart()
    {
        _server.CutAfterBytes = 500;
        _server.StallInsteadOfCut = true;
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(bytes =>
        {
            if (bytes >= 500)
            {
                cancel.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _downloader.DownloadAsync(_item, progress, cancel.Token));

        Assert.Equal(500, new FileInfo(ResumableDownloader.PartPath(_item)).Length);
        Assert.False(File.Exists(_item.Destination));
    }

    [Fact]
    public async Task Progress_CountsBytesOnDisk_UpToTheSize()
    {
        _server.CutAfterBytes = 300;
        var seen = new List<long>();

        await _downloader.DownloadAsync(_item, new SyncProgress(seen.Add), CancellationToken.None);

        Assert.Equal(Content.Length, seen[^1]);
        Assert.Equal(seen.Order(), seen);
    }

    private ResumableDownloader Downloader(TimeSpan stallTimeout) => new(_http, new DownloadOptions
    {
        FirstRetryDelay = TimeSpan.Zero,
        MaxRetryDelay = TimeSpan.Zero,
        MaxAttemptsWithoutProgress = 3,
        StallTimeout = stallTimeout,
    });

    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
