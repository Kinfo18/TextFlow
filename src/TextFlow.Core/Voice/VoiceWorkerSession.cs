using System.Collections.Concurrent;
using TextFlow.Contracts.Voice;

namespace TextFlow.Core.Voice;

/// <summary>
/// The protocol over one worker connection: handshake, a single reader that routes replies, and serialized writes.
/// Ends when the stream ends or carries garbage; <see cref="Completion"/> then says why.
/// </summary>
internal sealed class VoiceWorkerSession : IDisposable
{
    private readonly Stream _stream;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _pendingPings = new();
    private long _nextPingId;

    private VoiceWorkerSession(Stream stream, TimeProvider time)
    {
        _stream = stream;
        _time = time;
    }

    /// <summary>Completes with <see cref="VoiceWorkerExitReason.Crashed"/> or <see cref="VoiceWorkerExitReason.ProtocolError"/>.</summary>
    public Task<VoiceWorkerExitReason> Completion { get; private set; } = Task.FromResult(VoiceWorkerExitReason.Crashed);

    /// <exception cref="VoiceHandshakeException">No valid <see cref="ReadyMessage"/> in time.</exception>
    public static async Task<VoiceWorkerSession> OpenAsync(
        Stream stream, TimeProvider time, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var session = new VoiceWorkerSession(stream, time);

        // On timeout the read stays pending until the caller disposes the connection; that ends it.
        VoiceMessage? first;
        try
        {
            first = await HandshakeAsync(session, stream, cancellationToken)
                .WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidDataException)
        {
            session.Dispose();
            throw new VoiceHandshakeException("The voice worker did not answer the hello.", ex);
        }

        if (first is not ReadyMessage ready || ready.ProtocolVersion != VoiceProtocol.Version)
        {
            session.Dispose();
            throw new VoiceHandshakeException($"Unexpected handshake reply: {first?.GetType().Name ?? "end of stream"}.");
        }

        session.Completion = session.ReadLoopAsync();
        return session;
    }

    private static async Task<VoiceMessage?> HandshakeAsync(VoiceWorkerSession session, Stream stream, CancellationToken cancellationToken)
    {
        await session.SendAsync(new HelloMessage(VoiceProtocol.Version), cancellationToken).ConfigureAwait(false);
        return await VoiceFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Round trip of a ping.</summary>
    /// <exception cref="TimeoutException">No pong in <paramref name="timeout"/>: the worker is hung.</exception>
    /// <exception cref="VoiceWorkerUnavailableException">The session ended before the pong.</exception>
    public async Task<TimeSpan> PingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Completion.IsCompleted)
        {
            throw new VoiceWorkerUnavailableException();
        }

        var id = Interlocked.Increment(ref _nextPingId);
        var pong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPings[id] = pong;
        if (Completion.IsCompleted) // the read loop may have ended (and failed the pending pings) just before the insert
        {
            _pendingPings.TryRemove(id, out _);
            throw new VoiceWorkerUnavailableException();
        }

        var started = _time.GetTimestamp();
        try
        {
            await SendAsync(new PingMessage(id), cancellationToken).ConfigureAwait(false);
            await pong.Task.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
            return _time.GetElapsedTime(started);
        }
        catch (IOException ex)
        {
            throw new VoiceWorkerUnavailableException("The voice worker pipe broke.", ex);
        }
        finally
        {
            _pendingPings.TryRemove(id, out _);
        }
    }

    /// <exception cref="IOException">The pipe is broken or the session was disposed.</exception>
    public async Task SendAsync(VoiceMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The voice worker session has ended.", ex);
        }

        try
        {
            await VoiceFrame.WriteAsync(_stream, message, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The voice worker pipe is closed.", ex);
        }
        finally
        {
            try
            {
                _writeLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Session disposed while this write was still in flight (an abandoned shutdown send).
            }
        }
    }

    public void Dispose() => _writeLock.Dispose();

    private async Task<VoiceWorkerExitReason> ReadLoopAsync()
    {
        var reason = VoiceWorkerExitReason.Crashed;
        try
        {
            while (await VoiceFrame.ReadAsync(_stream, CancellationToken.None).ConfigureAwait(false) is { } message)
            {
                if (message is PongMessage pong && _pendingPings.TryGetValue(pong.Id, out var waiting))
                {
                    waiting.TrySetResult();
                }
            }
        }
        catch (InvalidDataException)
        {
            reason = VoiceWorkerExitReason.ProtocolError;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Pipe broken or closed under us: the process is gone or being killed.
        }

        foreach (var waiting in _pendingPings.Values)
        {
            waiting.TrySetException(new VoiceWorkerUnavailableException());
        }

        return reason;
    }
}

/// <summary>The worker connected but the hello/ready exchange failed.</summary>
public sealed class VoiceHandshakeException : Exception
{
    public VoiceHandshakeException()
    {
    }

    public VoiceHandshakeException(string message)
        : base(message)
    {
    }

    public VoiceHandshakeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
