using System.Collections.Concurrent;
using System.Threading.Channels;
using TextFlow.Contracts.Voice;
using TextFlow.Core.Voice;

namespace TextFlow.Core.Tests.Voice;

/// <summary>One direction of an in-memory pipe: writes on one side are read on the other.</summary>
internal sealed class ChannelStream(ChannelReader<byte[]> reader, ChannelWriter<byte[]> writer) : Stream
{
    private byte[] _current = [];
    private int _offset;

    public static (ChannelStream App, ChannelStream Worker) CreatePair()
    {
        var toWorker = Channel.CreateUnbounded<byte[]>();
        var toApp = Channel.CreateUnbounded<byte[]>();
        return (new ChannelStream(toApp.Reader, toWorker.Writer), new ChannelStream(toWorker.Reader, toApp.Writer));
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>Like a process dying: the other side reads end of stream.</summary>
    public void Close(Exception? error = null) => writer.TryComplete(error);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset == _current.Length)
        {
            if (!await reader.WaitToReadAsync(cancellationToken))
            {
                return 0;
            }

            if (reader.TryRead(out var next))
            {
                _current = next;
                _offset = 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!writer.TryWrite(buffer.ToArray()))
        {
            throw new IOException("Pipe closed.");
        }

        await Task.CompletedTask;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>How a <see cref="FakeWorker"/> behaves.</summary>
internal sealed record WorkerScript(
    bool AnswerHello = true,
    int ProtocolVersion = VoiceProtocol.Version,
    bool AnswerPings = true,
    bool ObeyShutdown = true);

/// <summary>A scripted worker on the other end of an in-memory pipe.</summary>
internal sealed class FakeWorker : IVoiceWorkerConnection
{
    private readonly ChannelStream _app;
    private readonly ChannelStream _worker;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeWorker(int processId, WorkerScript script)
    {
        ProcessId = processId;
        Script = script;
        (_app, _worker) = ChannelStream.CreatePair();
        _ = ServeAsync();
    }

    public int ProcessId { get; }

    public WorkerScript Script { get; set; }

    public Stream Stream => _app;

    public Task<int> Exited => _exited.Task;

    public bool Killed { get; private set; }

    public bool GotShutdown { get; private set; }

    public bool Disposed { get; private set; }

    public int PingsAnswered { get; private set; }

    /// <summary>The process dies on its own.</summary>
    public void Crash(int exitCode = 1) => Exit(exitCode);

    public void Kill()
    {
        Killed = true;
        Exit(-1);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _app.Close();
        return ValueTask.CompletedTask;
    }

    private void Exit(int code)
    {
        _worker.Close();
        _exited.TrySetResult(code);
    }

    private async Task ServeAsync()
    {
        try
        {
            while (await VoiceFrame.ReadAsync(_worker, CancellationToken.None) is { } message)
            {
                switch (message)
                {
                    case HelloMessage when Script.AnswerHello:
                        await VoiceFrame.WriteAsync(_worker, new ReadyMessage(Script.ProtocolVersion, ProcessId), CancellationToken.None);
                        break;
                    case PingMessage ping when Script.AnswerPings:
                        PingsAnswered++;
                        await VoiceFrame.WriteAsync(_worker, new PongMessage(ping.Id), CancellationToken.None);
                        break;
                    case ShutdownMessage:
                        GotShutdown = true;
                        if (Script.ObeyShutdown)
                        {
                            Exit(0);
                            return;
                        }

                        break;
                }
            }
        }
        catch (IOException)
        {
            // Our side closed.
        }
    }
}

internal sealed class FakeLauncher : IVoiceWorkerLauncher
{
    private readonly ConcurrentQueue<WorkerScript> _scripts = new();
    private readonly List<FakeWorker> _workers = [];
    private readonly Lock _gate = new();
    private int _failuresLeft;

    public WorkerScript DefaultScript { get; set; } = new();

    public IReadOnlyList<FakeWorker> Workers
    {
        get
        {
            lock (_gate)
            {
                return _workers.ToArray();
            }
        }
    }

    public int Attempts { get; private set; }

    public FakeWorker Last => Workers[^1];

    /// <summary>The next launch uses <paramref name="script"/> instead of <see cref="DefaultScript"/>.</summary>
    public void Enqueue(WorkerScript script) => _scripts.Enqueue(script);

    /// <summary>The next <paramref name="count"/> launches throw, like a missing exe.</summary>
    public void FailNext(int count) => _failuresLeft = count;

    public Task<IVoiceWorkerConnection> LaunchAsync(CancellationToken cancellationToken)
    {
        Attempts++;
        if (_failuresLeft > 0)
        {
            _failuresLeft--;
            throw new FileNotFoundException("worker missing");
        }

        var worker = new FakeWorker(1000 + Attempts, _scripts.TryDequeue(out var script) ? script : DefaultScript);
        lock (_gate)
        {
            _workers.Add(worker);
        }

        return Task.FromResult<IVoiceWorkerConnection>(worker);
    }
}

internal static class Eventually
{
    /// <summary>Polls in real time: the supervisor runs on the thread pool.</summary>
    public static async Task TrueAsync(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail($"Timed out waiting until {because}.");
            }

            await Task.Delay(5);
        }
    }
}
