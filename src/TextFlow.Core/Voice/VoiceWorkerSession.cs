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
    private readonly ConcurrentDictionary<long, TaskCompletionSource<VoiceReply>> _pending = new();
    private long _nextRequestId;

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
        var started = _time.GetTimestamp();
        await RequestAsync<PongReply>(id => new PingRequest(id), timeout, cancellationToken).ConfigureAwait(false);
        return _time.GetElapsedTime(started);
    }

    /// <summary>Sends the request built from a fresh id and waits for the reply with that id.</summary>
    /// <exception cref="VoiceRequestException">The worker answered with an <see cref="ErrorReply"/>.</exception>
    /// <exception cref="TimeoutException">No reply in <paramref name="timeout"/>.</exception>
    /// <exception cref="VoiceWorkerUnavailableException">The session ended before the reply.</exception>
    /// <exception cref="InvalidDataException">The worker answered with another kind of reply.</exception>
    public async Task<TReply> RequestAsync<TReply>(
        Func<long, VoiceRequest> create, TimeSpan timeout, CancellationToken cancellationToken)
        where TReply : VoiceReply
    {
        ArgumentNullException.ThrowIfNull(create);
        if (Completion.IsCompleted)
        {
            throw new VoiceWorkerUnavailableException();
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var reply = new TaskCompletionSource<VoiceReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        if (Completion.IsCompleted) // the read loop may have ended (and failed the pending requests) just before the insert
        {
            _pending.TryRemove(id, out _);
            throw new VoiceWorkerUnavailableException();
        }

        VoiceReply answer;
        try
        {
            await SendAsync(create(id), cancellationToken).ConfigureAwait(false);
            answer = await reply.Task.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new VoiceWorkerUnavailableException("The voice worker pipe broke.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }

        return answer switch
        {
            TReply expected => expected,
            ErrorReply error => throw new VoiceRequestException(error.Code),
            _ => throw new InvalidDataException($"Expected {typeof(TReply).Name}, got {answer.GetType().Name}."),
        };
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
            // Never cancelled once started: half a frame on the pipe would desync the worker for good.
            await VoiceFrame.WriteAsync(_stream, message, CancellationToken.None).ConfigureAwait(false);
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
                if (message is VoiceReply reply && _pending.TryGetValue(reply.Id, out var waiting))
                {
                    waiting.TrySetResult(reply);
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

        foreach (var waiting in _pending.Values)
        {
            waiting.TrySetException(new VoiceWorkerUnavailableException());
        }

        return reason;
    }
}

/// <summary>The worker could not do what was asked; <see cref="Code"/> says why (no microphone, access denied…).</summary>
public sealed class VoiceRequestException : Exception
{
    public VoiceRequestException()
    {
    }

    public VoiceRequestException(VoiceErrorCode code)
        : base($"The voice worker refused the request: {code}.")
    {
        Code = code;
    }

    public VoiceRequestException(string message)
        : base(message)
    {
    }

    public VoiceRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VoiceErrorCode Code { get; }
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
