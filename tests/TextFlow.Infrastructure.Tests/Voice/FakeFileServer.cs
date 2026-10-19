using System.Net;
using System.Net.Http.Headers;

namespace TextFlow.Infrastructure.Tests.Voice;

/// <summary>
/// An HTTP server in memory for the downloader tests: serves files by URL with <c>Range</c> support, and can cut
/// connections, stall, fail with a status or ignore ranges, like this PC's network and real servers do.
/// </summary>
internal sealed class FakeFileServer : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly List<(string Path, long? From)> _requests = [];

    /// <summary>Each response sends at most this many bytes, then the connection breaks.</summary>
    public int CutAfterBytes { get; set; } = int.MaxValue;

    /// <summary>After <see cref="CutAfterBytes"/>, hang instead of breaking (a stalled connection).</summary>
    public bool StallInsteadOfCut { get; set; }

    /// <summary>Answer 200 with the whole file even when asked for a range.</summary>
    public bool IgnoreRange { get; set; }

    /// <summary>The next ranged answer starts one byte later than asked (a confused proxy).</summary>
    public bool ShiftRangeOnce { get; set; }

    /// <summary>Statuses answered before serving, one per request.</summary>
    public Queue<HttpStatusCode> Statuses { get; } = new();

    public IReadOnlyList<(string Path, long? From)> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public Uri Add(string path, byte[] content)
    {
        _files[path] = content;
        return new Uri("https://files.test/" + path);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var from = request.Headers.Range?.Ranges.Single().From;
        lock (_gate)
        {
            _requests.Add((path, from));
        }

        if (Statuses.TryDequeue(out var status))
        {
            return Task.FromResult(new HttpResponseMessage(status));
        }

        if (!_files.TryGetValue(path, out var content))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var start = IgnoreRange ? 0 : from ?? 0;
        if (start > 0 && ShiftRangeOnce)
        {
            ShiftRangeOnce = false;
            start++;
        }

        if (start >= content.Length)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        }

        var body = new StreamContent(new CuttingStream(content, (int)start, CutAfterBytes, StallInsteadOfCut));
        body.Headers.ContentLength = content.Length - start;
        var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = body };
        if (start > 0)
        {
            body.Headers.ContentRange = new ContentRangeHeaderValue(start, content.Length - 1, content.Length);
        }

        return Task.FromResult(response);
    }

    private sealed class CuttingStream(byte[] content, int start, int cutAfter, bool stall) : Stream
    {
        private int _position = start;
        private int _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= content.Length)
            {
                return 0;
            }

            if (_sent >= cutAfter)
            {
                if (stall)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                throw new IOException("Connection reset (test).");
            }

            var count = Math.Min(Math.Min(buffer.Length, content.Length - _position), cutAfter - _sent);
            content.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            _sent += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
