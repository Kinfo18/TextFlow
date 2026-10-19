using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace TextFlow.Infrastructure.Voice;

/// <summary>A file to fetch: where from, how big, its SHA-256 (hex) and where it ends up.</summary>
public sealed record DownloadItem(Uri Source, long Size, string Sha256, string Destination);

public sealed record DownloadOptions
{
    /// <summary>Attempts in a row that add no byte before giving up; any progress resets the count.</summary>
    public int MaxAttemptsWithoutProgress { get; init; } = 8;

    /// <summary>Wait before the first retry; it doubles up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>No byte for this long and the connection is taken as dead (this PC's network stalls, not only cuts).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public enum DownloadFailure
{
    Unknown,
    /// <summary>The finished file is not the expected one: it was deleted.</summary>
    HashMismatch,
    /// <summary>The server sends more than the expected size: wrong file, nothing kept.</summary>
    SizeMismatch,
    /// <summary>The server refuses for good (404, 403…).</summary>
    Refused,
    /// <summary>The network kept failing without progress; what arrived is kept for the next try.</summary>
    NetworkGaveUp,
    /// <summary>Writing on this PC failed (disk full, file locked by an antivirus, no permission).</summary>
    Disk,
}

public sealed class DownloadException : Exception
{
    public DownloadException()
    {
    }

    public DownloadException(DownloadFailure failure, Exception? innerException = null)
        : base($"Download failed: {failure}.", innerException)
    {
        Failure = failure;
    }

    public DownloadException(string message)
        : base(message)
    {
    }

    public DownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DownloadFailure Failure { get; }
}

/// <summary>
/// Downloads big files over a network that cuts and stalls (V0.2 H1.4, ADR-0009 §H0.5): into <c>.part</c> next to the
/// destination, resuming with <c>Range</c> after every failure (each retry asks the original URL again: Hugging Face
/// redirects to signed CDN URLs that expire), then checks the SHA-256 and renames it into place. A <c>.part</c> left by
/// a closed app resumes on the next call. One call per file at a time: the caller serializes them.
/// </summary>
public sealed class ResumableDownloader
{
    private const int BufferBytes = 1 << 20;

    private readonly HttpClient _http;
    private readonly DownloadOptions _options;
    private readonly TimeProvider _time;

    public ResumableDownloader(HttpClient http, DownloadOptions? options = null, TimeProvider? time = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? new DownloadOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Partial file of <paramref name="item"/>.</summary>
    public static string PartPath(DownloadItem item) => (item ?? throw new ArgumentNullException(nameof(item))).Destination + ".part";

    /// <summary>
    /// Ends with the verified file at <see cref="DownloadItem.Destination"/>. A file already there is hashed first and,
    /// if it is not the expected one (an older version, a damaged copy), downloaded again.
    /// </summary>
    /// <param name="progress">Bytes of the file already on disk, as they grow.</param>
    /// <exception cref="DownloadException">Wrong hash or size, refused, the network gave up, or the disk failed.</exception>
    public async Task DownloadAsync(DownloadItem item, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            if (File.Exists(item.Destination))
            {
                if (await HasHashAsync(item.Destination, item.Sha256, cancellationToken).ConfigureAwait(false))
                {
                    progress?.Report(item.Size);
                    return;
                }

                File.Delete(item.Destination);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(item.Destination))!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DownloadException(DownloadFailure.Disk, ex);
        }

        var part = PartPath(item);
        await FillPartAsync(item, part, progress, cancellationToken).ConfigureAwait(false);

        try
        {
            if (!await HasHashAsync(part, item.Sha256, cancellationToken).ConfigureAwait(false))
            {
                File.Delete(part);
                throw new DownloadException(DownloadFailure.HashMismatch);
            }

            File.Move(part, item.Destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DownloadException(DownloadFailure.Disk, ex);
        }

        progress?.Report(item.Size);
    }

    /// <summary>Retries until the <c>.part</c> has the whole size, while each attempt keeps adding bytes.</summary>
    private async Task FillPartAsync(DownloadItem item, string part, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var attemptsWithoutProgress = 0;
        var delay = _options.FirstRetryDelay;
        while (true)
        {
            var before = Length(part);
            if (before == item.Size)
            {
                return;
            }

            if (before > item.Size)
            {
                // Left by another version of the file: it cannot be the start of this one.
                DeleteQuietly(part);
                before = 0;
            }

            try
            {
                await FetchAsync(item, part, before, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                // A server that ignores Range and cuts before the old length shows as no progress: the cap stops it.
                if (Length(part) > before)
                {
                    attemptsWithoutProgress = 0;
                    delay = _options.FirstRetryDelay;
                }
                else if (++attemptsWithoutProgress >= _options.MaxAttemptsWithoutProgress)
                {
                    throw new DownloadException(DownloadFailure.NetworkGaveUp, ex);
                }

                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                delay = delay * 2 < _options.MaxRetryDelay ? delay * 2 : _options.MaxRetryDelay;
            }
        }
    }

    private async Task FetchAsync(DownloadItem item, string part, long have, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_options.StallTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, item.Source);
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            DeleteQuietly(part); // the .part does not match what the server has: start over
            throw new HttpRequestException("Range not satisfiable.", null, response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw IsRetryable(response.StatusCode)
                ? new HttpRequestException($"HTTP {(int)response.StatusCode}.", null, response.StatusCode)
                : new DownloadException(DownloadFailure.Refused);
        }

        long offset;
        long? total;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != have)
            {
                // Not the part that was asked for: keep the .part and ask again.
                throw new HttpRequestException("Unexpected Content-Range.", null, response.StatusCode);
            }

            offset = have;
            total = range.Length;
        }
        else
        {
            offset = 0; // a server that ignores Range sends the whole file: start from zero
            total = response.Content.Headers.ContentLength;
        }

        if (total is { } length && length != item.Size)
        {
            DeleteQuietly(part);
            throw new DownloadException(DownloadFailure.SizeMismatch);
        }

        await using var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
        await using var file = OpenPart(part, append: offset > 0);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            var written = offset;
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, BufferBytes), stall.Token).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > item.Size)
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                    DeleteQuietly(part);
                    throw new DownloadException(DownloadFailure.SizeMismatch);
                }

                await WriteAsync(file, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                stall.CancelAfter(_options.StallTimeout);
                progress?.Report(written);
            }

            await WriteAsync(file, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            if (written < item.Size)
            {
                throw new IOException("The connection ended before the whole file arrived.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Opening the part fails as <see cref="DownloadFailure.Disk"/>, never as a retryable network error.</summary>
    private static FileStream OpenPart(string part, bool append)
    {
        try
        {
            return new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, BufferBytes, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DownloadException(DownloadFailure.Disk, ex);
        }
    }

    /// <summary>Writes <paramref name="data"/>, or flushes when it is empty; disk errors become <see cref="DownloadFailure.Disk"/>.</summary>
    private static async Task WriteAsync(FileStream file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            if (data.IsEmpty)
            {
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await file.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DownloadException(DownloadFailure.Disk, ex);
        }
    }

    private static async Task<bool> HasHashAsync(string path, string sha256, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, useAsync: true);
        var hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next attempt: a .part that does not match fails the hash check and is deleted then.
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        DownloadException => false,
        OperationCanceledException => !cancellationToken.IsCancellationRequested, // a stall, not the caller
        HttpRequestException or IOException => true, // file errors are DownloadException(Disk) by now
        _ => false,
    };

    private static bool IsRetryable(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
