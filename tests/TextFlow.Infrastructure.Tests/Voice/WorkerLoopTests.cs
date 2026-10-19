using System.IO.Pipes;
using TextFlow.Contracts.Voice;
using TextFlow.VoiceWorker;
using TextFlow.VoiceWorker.Audio;
using TextFlow.VoiceWorker.Speech;

namespace TextFlow.Infrastructure.Tests.Voice;

/// <summary>The worker's request handling (V0.2 H1.2) with a fake microphone, over in-process pipes.</summary>
public sealed class WorkerLoopTests : IAsyncDisposable
{
    private readonly FakeMicrophones _microphones = new();
    private readonly FakeSpeechModels _models = new();
    private readonly CaptureBuffer _buffer = new(capacitySamples: 16000);
    private readonly AnonymousPipeServerStream _toWorker = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _toApp = new(PipeDirection.Out);
    private readonly Stream _workerSide;
    private readonly Stream _appSide;
    private readonly WorkerLoop _loop;
    private readonly Task<int> _running;
    private long _nextId;

    public WorkerLoopTests()
    {
        _workerSide = new Duplex(new AnonymousPipeClientStream(PipeDirection.In, _toWorker.ClientSafePipeHandle), _toApp);
        _appSide = new Duplex(new AnonymousPipeClientStream(PipeDirection.In, _toApp.ClientSafePipeHandle), _toWorker);
        _loop = new WorkerLoop(_workerSide, _microphones, _models, _buffer);
        _running = Task.Run(() => _loop.RunAsync(CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        _appSide.Dispose();
        await _running.WaitAsync(TimeSpan.FromSeconds(5));
        _loop.Dispose();
        _workerSide.Dispose();
        _models.Dispose();
    }

    [Fact]
    public async Task Hello_IsAnsweredWithReady()
    {
        var ready = await HelloAsync();

        Assert.Equal(VoiceProtocol.Version, ready.ProtocolVersion);
        Assert.Equal(Environment.ProcessId, ready.ProcessId);
    }

    [Fact]
    public async Task ListMicrophones_ReturnsWhatWindowsHas()
    {
        await HelloAsync();

        var reply = await RequestAsync(id => new ListMicrophonesRequest(id));

        Assert.Equal(FakeMicrophones.All, Assert.IsType<MicrophonesReply>(reply).Microphones);
    }

    [Fact]
    public async Task StartThenStop_ReportsTheCapturedAudio_AndForgetsIt()
    {
        await HelloAsync();

        var started = await RequestAsync(id => new StartCaptureRequest(id, "mic-2"));
        _microphones.Hear(new byte[8000 * 2]); // half a second of silence…
        _microphones.Hear([0x00, 0x40]); // …and one sample at half scale
        var stopped = await RequestAsync(id => new StopCaptureRequest(id));

        Assert.Equal("USB", Assert.IsType<CaptureStartedReply>(started).MicrophoneName);
        Assert.Equal("mic-2", _microphones.LastRequestedId);
        var result = Assert.IsType<CaptureStoppedReply>(stopped);
        Assert.Equal(8001 * 1000.0 / 16000, result.DurationMs);
        Assert.Equal(0.5, result.PeakLevel);
        Assert.False(result.Truncated);
        Assert.True(_microphones.Current!.Disposed);
        Assert.Equal(0, _buffer.Count); // not kept: nothing consumes it until H2
    }

    [Fact]
    public async Task StartWhileCapturing_ClosesTheOldCapture_AndDiscardsItsAudio()
    {
        await HelloAsync();
        await RequestAsync(id => new StartCaptureRequest(id, null));
        var abandoned = _microphones.Current!;
        _microphones.Hear(new byte[1600 * 2]);

        var second = await RequestAsync(id => new StartCaptureRequest(id, "mic-2"));
        var stopped = await RequestAsync(id => new StopCaptureRequest(id));

        Assert.IsType<CaptureStartedReply>(second);
        Assert.True(abandoned.Disposed);
        Assert.Equal(0, Assert.IsType<CaptureStoppedReply>(stopped).DurationMs);
    }

    [Fact]
    public async Task UnexpectedMicrophoneException_IsAnUnknownError_NotACrash()
    {
        await HelloAsync();
        _microphones.Throw = new InvalidOperationException("driver weirdness");

        var reply = await RequestAsync(id => new StartCaptureRequest(id, null));

        Assert.Equal(VoiceErrorCode.Unknown, Assert.IsType<ErrorReply>(reply).Code);
        Assert.IsType<PongReply>(await RequestAsync(id => new PingRequest(id)));
    }

    [Fact]
    public async Task StopWithoutStart_IsRefused()
    {
        await HelloAsync();

        var reply = await RequestAsync(id => new StopCaptureRequest(id));

        Assert.Equal(VoiceErrorCode.NotCapturing, Assert.IsType<ErrorReply>(reply).Code);
    }

    [Theory]
    [InlineData(VoiceErrorCode.NoMicrophone)]
    [InlineData(VoiceErrorCode.MicrophoneAccessDenied)]
    [InlineData(VoiceErrorCode.MicrophoneNotFound)]
    public async Task MicrophoneFailure_IsAnErrorReply_AndTheWorkerKeepsServing(VoiceErrorCode code)
    {
        await HelloAsync();
        _microphones.FailWith = code;

        var reply = await RequestAsync(id => new StartCaptureRequest(id, null));

        Assert.Equal(code, Assert.IsType<ErrorReply>(reply).Code);
        Assert.IsType<PongReply>(await RequestAsync(id => new PingRequest(id)));
    }

    [Fact]
    public async Task MicrophoneLostWhileRecording_KeepsTheAudioUpToThen_AndAllowsANewStart()
    {
        await HelloAsync();
        await RequestAsync(id => new StartCaptureRequest(id, null));
        _microphones.Hear(new byte[8000 * 2]);
        _microphones.Current!.LoseDevice();

        var stopped = await RequestAsync(id => new StopCaptureRequest(id));
        var again = await RequestAsync(id => new StartCaptureRequest(id, null));

        var result = Assert.IsType<CaptureStoppedReply>(stopped);
        Assert.True(result.MicrophoneLost);
        Assert.Equal(500, result.DurationMs);
        Assert.IsType<CaptureStartedReply>(again);
    }

    [Fact]
    public async Task ReplyIds_MatchTheRequests()
    {
        await HelloAsync();

        var reply = await RequestAsync(_ => new PingRequest(424242));

        Assert.Equal(424242, reply.Id);
    }

    [Fact]
    public async Task LoadModel_ReportsTheRuntimeAndTimes()
    {
        await HelloAsync();

        var reply = Assert.IsType<ModelLoadedReply>(await RequestAsync(id => new LoadModelRequest(id, "turbo.bin", @"C:\pack")));

        Assert.Equal(("Cuda12", 700.0, 400.0, false), (reply.Runtime, reply.LoadMs, reply.WarmupMs, reply.WasLoaded));
        Assert.Equal(("turbo.bin", @"C:\pack"), _models.LastRequest);
    }

    [Fact]
    public async Task LoadModel_Again_IsAnsweredAsLoaded_WithoutLoadingTwice()
    {
        await HelloAsync();
        await RequestAsync(id => new LoadModelRequest(id, "turbo.bin", null));

        var again = Assert.IsType<ModelLoadedReply>(await RequestAsync(id => new LoadModelRequest(id, "turbo.bin", null)));

        Assert.True(again.WasLoaded);
        Assert.Equal(1, _models.Loads);
    }

    [Fact]
    public async Task Pings_AreAnswered_WhileTheModelLoads()
    {
        await HelloAsync();
        _models.Hold();

        await VoiceFrame.WriteAsync(_appSide, new LoadModelRequest(++_nextId, "turbo.bin", null), CancellationToken.None);
        var loadId = _nextId;
        var pong = await RequestAsync(id => new PingRequest(id));

        Assert.IsType<PongReply>(pong);
        _models.Release();
        var loaded = Assert.IsType<ModelLoadedReply>(await ReadAsync());
        Assert.Equal(loadId, loaded.Id);
    }

    [Fact]
    public async Task SecondLoad_WhileLoading_JoinsTheSameLoad()
    {
        await HelloAsync();
        _models.Hold();

        await VoiceFrame.WriteAsync(_appSide, new LoadModelRequest(1, "turbo.bin", null), CancellationToken.None);
        await VoiceFrame.WriteAsync(_appSide, new LoadModelRequest(2, "turbo.bin", null), CancellationToken.None);
        await Task.Delay(50);
        _models.Release();
        var replies = new[] { await ReadAsync(), await ReadAsync() }.Cast<ModelLoadedReply>().OrderBy(r => r.Id).ToArray();

        Assert.Equal(1, _models.Loads);
        Assert.Equal([false, true], replies.Select(r => r.WasLoaded));
    }

    [Theory]
    [InlineData(VoiceErrorCode.ModelNotFound)]
    [InlineData(VoiceErrorCode.NoGpu)]
    [InlineData(VoiceErrorCode.ModelLoadFailed)]
    public async Task ModelFailure_IsAnErrorReply_AndALaterLoadTriesAgain(VoiceErrorCode code)
    {
        await HelloAsync();
        _models.FailWith = code;

        var error = Assert.IsType<ErrorReply>(await RequestAsync(id => new LoadModelRequest(id, "turbo.bin", null)));
        _models.FailWith = null;
        var retry = await RequestAsync(id => new LoadModelRequest(id, "turbo.bin", null));

        Assert.Equal(code, error.Code);
        Assert.False(Assert.IsType<ModelLoadedReply>(retry).WasLoaded);
    }

    [Fact]
    public async Task UnexpectedLoadException_IsAnUnknownError_AndTheWorkerKeepsServing()
    {
        await HelloAsync();
        _models.Throw = new InvalidOperationException("native boom");

        var error = Assert.IsType<ErrorReply>(await RequestAsync(id => new LoadModelRequest(id, "turbo.bin", null)));

        Assert.Equal(VoiceErrorCode.Unknown, error.Code);
        Assert.IsType<PongReply>(await RequestAsync(id => new PingRequest(id)));
    }

    [Fact]
    public async Task AnotherModelFile_FreesTheOldOne_AndLoadsTheNew()
    {
        await HelloAsync();
        await RequestAsync(id => new LoadModelRequest(id, "turbo.bin", null));
        var old = _models.Current!;

        var reply = Assert.IsType<ModelLoadedReply>(await RequestAsync(id => new LoadModelRequest(id, "small.bin", null)));

        Assert.False(reply.WasLoaded);
        Assert.True(old.Disposed);
        Assert.Equal(2, _models.Loads);
    }

    [Fact]
    public async Task Dispose_FreesTheLoadedModel()
    {
        await HelloAsync();
        await RequestAsync(id => new LoadModelRequest(id, "turbo.bin", null));

        _loop.Dispose();

        Assert.True(_models.Current!.Disposed);
    }

    [Fact]
    public async Task Shutdown_EndsTheLoopCleanly()
    {
        await HelloAsync();

        await VoiceFrame.WriteAsync(_appSide, new ShutdownMessage(), CancellationToken.None);

        Assert.Equal(WorkerExitCode.Ok, await _running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private async Task<ReadyMessage> HelloAsync()
    {
        await VoiceFrame.WriteAsync(_appSide, new HelloMessage(VoiceProtocol.Version), CancellationToken.None);
        return Assert.IsType<ReadyMessage>(await ReadAsync());
    }

    private async Task<VoiceReply> RequestAsync(Func<long, VoiceRequest> create)
    {
        await VoiceFrame.WriteAsync(_appSide, create(++_nextId), CancellationToken.None);
        return Assert.IsAssignableFrom<VoiceReply>(await ReadAsync());
    }

    private Task<VoiceMessage?> ReadAsync() =>
        VoiceFrame.ReadAsync(_appSide, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>Two one-way anonymous pipes as one duplex stream.</summary>
    private sealed class Duplex(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            read.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            write.WriteAsync(buffer, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);

        public override void Flush() => write.Flush();

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FakeMicrophones : IMicrophones
    {
        public static readonly MicrophoneInfo[] All = [new("mic-1", "Portátil", true), new("mic-2", "USB", false)];

        public VoiceErrorCode? FailWith { get; set; }

        public Exception? Throw { get; set; }

        public string? LastRequestedId { get; private set; }

        public FakeCapture? Current { get; private set; }

        private CaptureBuffer? _buffer;

        public IReadOnlyList<MicrophoneInfo> List() => All;

        public IMicrophoneCapture Start(string? microphoneId, CaptureBuffer buffer)
        {
            if (FailWith is { } code)
            {
                throw new MicrophoneException(code);
            }

            if (Throw is { } error)
            {
                throw error;
            }

            LastRequestedId = microphoneId;
            _buffer = buffer;
            Current = new FakeCapture(All.SingleOrDefault(m => m.Id == microphoneId)?.Name ?? "Portátil");
            return Current;
        }

        public void Hear(byte[] pcm) => _buffer!.WritePcm16(pcm);
    }

    private sealed class FakeSpeechModels : ISpeechModels, IDisposable
    {
        private readonly ManualResetEventSlim _open = new(initialState: true);
        private int _loads;

        public VoiceErrorCode? FailWith { get; set; }

        public Exception? Throw { get; set; }

        public int Loads => Volatile.Read(ref _loads);

        public (string Model, string? Pack) LastRequest { get; private set; }

        public FakeModel? Current { get; private set; }

        /// <summary>Loads block until <see cref="Release"/>, like a model that takes a while.</summary>
        public void Hold() => _open.Reset();

        public void Release() => _open.Set();

        public void Dispose() => _open.Dispose();

        public ILoadedSpeechModel Load(string modelPath, string? gpuPackPath)
        {
            _open.Wait(TimeSpan.FromSeconds(5));
            LastRequest = (modelPath, gpuPackPath);
            if (FailWith is { } code)
            {
                throw new SpeechModelException(code);
            }

            if (Throw is { } error)
            {
                throw error;
            }

            Interlocked.Increment(ref _loads);
            Current = new FakeModel();
            return Current;
        }
    }

    private sealed class FakeModel : ILoadedSpeechModel
    {
        public string Runtime => "Cuda12";

        public double LoadMs => 700;

        public double WarmupMs => 400;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeCapture(string name) : IMicrophoneCapture
    {
        private bool _lost;

        public string Name => name;

        public bool Disposed { get; private set; }

        public void LoseDevice() => _lost = true;

        public bool Stop() => !_lost;

        public void Dispose() => Disposed = true;
    }
}
