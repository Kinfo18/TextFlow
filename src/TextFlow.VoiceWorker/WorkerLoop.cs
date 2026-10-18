using TextFlow.Contracts.Voice;
using TextFlow.VoiceWorker.Audio;

namespace TextFlow.VoiceWorker;

/// <summary>
/// The worker side of the protocol: answers the hello, then serves requests one at a time until told to stop.
/// Audio stays here: the app only gets durations and levels.
/// </summary>
internal sealed class WorkerLoop : IDisposable
{
    private readonly Stream _pipe;
    private readonly IMicrophones _microphones;
    private readonly CaptureBuffer _buffer;
    private IMicrophoneCapture? _capture;

    public WorkerLoop(Stream pipe, IMicrophones microphones, CaptureBuffer? buffer = null)
    {
        _pipe = pipe;
        _microphones = microphones;
        _buffer = buffer ?? new CaptureBuffer();
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await VoiceFrame.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false) is not HelloMessage hello
                || hello.ProtocolVersion != VoiceProtocol.Version)
            {
                return WorkerExitCode.ProtocolError;
            }

            await SendAsync(new ReadyMessage(VoiceProtocol.Version, Environment.ProcessId), cancellationToken).ConfigureAwait(false);

            while (true)
            {
                switch (await VoiceFrame.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false))
                {
                    case null: // TextFlow closed the pipe
                    case ShutdownMessage:
                        return WorkerExitCode.Ok;
                    case VoiceRequest request:
                        await SendAsync(Handle(request), cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        return WorkerExitCode.ProtocolError;
                }
            }
        }
        catch (InvalidDataException)
        {
            return WorkerExitCode.ProtocolError;
        }
        catch (IOException)
        {
            return WorkerExitCode.PipeBroken;
        }
        catch (OperationCanceledException)
        {
            return WorkerExitCode.ParentGone;
        }
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _capture = null;
        _buffer.Clear();
    }

    private VoiceReply Handle(VoiceRequest request)
    {
        try
        {
            return request switch
            {
                PingRequest => new PongReply(request.Id),
                ListMicrophonesRequest => new MicrophonesReply(request.Id, _microphones.List()),
                StartCaptureRequest start => StartCapture(start),
                StopCaptureRequest => StopCapture(request.Id),
                _ => new ErrorReply(request.Id, VoiceErrorCode.UnknownRequest),
            };
        }
        catch (MicrophoneException ex)
        {
            return new ErrorReply(request.Id, ex.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A bug or an unexpected driver error answers this request instead of crashing the worker mid-dictation.
            return new ErrorReply(request.Id, VoiceErrorCode.Unknown);
        }
    }

    private CaptureStartedReply StartCapture(StartCaptureRequest request)
    {
        // The app gave up on an earlier start (timeout): close that one instead of refusing, never leave it open.
        if (_capture is { } stale)
        {
            _capture = null;
            stale.Dispose();
        }

        _buffer.Clear();
        _capture = _microphones.Start(request.MicrophoneId, _buffer);
        return new CaptureStartedReply(request.Id, _capture.Name);
    }

    private VoiceReply StopCapture(long id)
    {
        if (_capture is not { } capture)
        {
            return new ErrorReply(id, VoiceErrorCode.NotCapturing);
        }

        _capture = null;
        try
        {
            var intact = capture.Stop();
            return new CaptureStoppedReply(id, _buffer.DurationMs, _buffer.PeakLevel, _buffer.Truncated, MicrophoneLost: !intact);
        }
        finally
        {
            capture.Dispose();

            // Nothing consumes the audio yet: H2 transcribes it here first. Until then it is not kept (ADR-0006).
            _buffer.Clear();
        }
    }

    private Task SendAsync(VoiceMessage message, CancellationToken cancellationToken) =>
        VoiceFrame.WriteAsync(_pipe, message, cancellationToken);
}
