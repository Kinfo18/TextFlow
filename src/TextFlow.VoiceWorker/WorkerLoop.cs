using TextFlow.Contracts.Voice;
using TextFlow.VoiceWorker.Audio;
using TextFlow.VoiceWorker.Speech;

namespace TextFlow.VoiceWorker;

/// <summary>
/// The worker side of the protocol: answers the hello, then serves requests in order until told to stop. Loading the
/// model is the exception: it runs on the thread pool and answers when done, so pings and captures go on meanwhile.
/// Audio stays here: the app only gets durations and levels.
/// </summary>
internal sealed class WorkerLoop : IDisposable
{
    private readonly Stream _pipe;
    private readonly IMicrophones _microphones;
    private readonly ISpeechModels _models;
    private readonly CaptureBuffer _buffer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _modelGate = new();
    private IMicrophoneCapture? _capture;
    private Task<ILoadedSpeechModel>? _model;
    private string? _modelPath;

    public WorkerLoop(Stream pipe, IMicrophones microphones, ISpeechModels models, CaptureBuffer? buffer = null)
    {
        _pipe = pipe;
        _microphones = microphones;
        _models = models;
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
                    case LoadModelRequest load:
                        _ = ReplyWhenLoadedAsync(load, cancellationToken);
                        break;
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

        // A load still running finishes on its own (native code cannot be interrupted); the process is ending anyway.
        _model?.ContinueWith(
            load => load.Result.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _model = null;
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

    private async Task ReplyWhenLoadedAsync(LoadModelRequest request, CancellationToken cancellationToken)
    {
        VoiceReply reply;
        try
        {
            var (load, joined) = LoadOnce(request);
            var model = await load.ConfigureAwait(false);
            reply = joined
                ? new ModelLoadedReply(request.Id, model.Runtime, 0, 0, WasLoaded: true)
                : new ModelLoadedReply(request.Id, model.Runtime, model.LoadMs, model.WarmupMs, WasLoaded: false);
        }
        catch (SpeechModelException ex)
        {
            reply = new ErrorReply(request.Id, ex.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reply = new ErrorReply(request.Id, VoiceErrorCode.Unknown);
        }

        try
        {
            await SendAsync(reply, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The pipe is gone: the loop sees it too and ends the worker.
        }
    }

    /// <summary>The load of that model: the one already done or running (<c>joined</c>), or a new one.</summary>
    private (Task<ILoadedSpeechModel> Load, bool Joined) LoadOnce(LoadModelRequest request)
    {
        lock (_modelGate)
        {
            if (_model is { IsFaulted: false, IsCanceled: false } current && _modelPath == request.ModelPath)
            {
                return (current, true);
            }

            var previous = _model;
            _modelPath = request.ModelPath;
            _model = Task.Run(async () =>
            {
                if (previous is not null)
                {
                    // Another model file: free the old one first, there is no VRAM for both.
                    try
                    {
                        (await previous.ConfigureAwait(false)).Dispose();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // It never loaded: nothing to free.
                    }
                }

                return _models.Load(request.ModelPath, request.GpuPackPath);
            });
            return (_model, false);
        }
    }

    /// <summary>Whole frames only, one at a time: the loop and a finished model load both answer on this pipe.</summary>
    private async Task SendAsync(VoiceMessage message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VoiceFrame.WriteAsync(_pipe, message, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
