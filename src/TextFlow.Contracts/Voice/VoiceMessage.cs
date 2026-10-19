using System.Text.Json.Serialization;

namespace TextFlow.Contracts.Voice;

/// <summary>
/// A message on the named pipe between TextFlow and <c>TextFlow.VoiceWorker</c> (V0.2 H1.1). Only control and text
/// cross the pipe: audio is captured and kept inside the worker (spec §7, ADR-0006). <see cref="VoiceFrame"/> carries
/// one per frame.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(ReadyMessage), "ready")]
[JsonDerivedType(typeof(ShutdownMessage), "shutdown")]
[JsonDerivedType(typeof(PingRequest), "ping")]
[JsonDerivedType(typeof(PongReply), "pong")]
[JsonDerivedType(typeof(ListMicrophonesRequest), "list-microphones")]
[JsonDerivedType(typeof(MicrophonesReply), "microphones")]
[JsonDerivedType(typeof(StartCaptureRequest), "start-capture")]
[JsonDerivedType(typeof(CaptureStartedReply), "capture-started")]
[JsonDerivedType(typeof(StopCaptureRequest), "stop-capture")]
[JsonDerivedType(typeof(CaptureStoppedReply), "capture-stopped")]
[JsonDerivedType(typeof(LoadModelRequest), "load-model")]
[JsonDerivedType(typeof(ModelLoadedReply), "model-loaded")]
[JsonDerivedType(typeof(ErrorReply), "error")]
public abstract record VoiceMessage;

/// <summary>App → worker, first message: the protocol the app speaks.</summary>
public sealed record HelloMessage(int ProtocolVersion) : VoiceMessage;

/// <summary>Worker → app, answer to <see cref="HelloMessage"/>: the worker is up and speaks <paramref name="ProtocolVersion"/>.</summary>
public sealed record ReadyMessage(int ProtocolVersion, int ProcessId) : VoiceMessage;

/// <summary>App → worker: finish and exit.</summary>
public sealed record ShutdownMessage : VoiceMessage;

/// <summary>App → worker; the worker answers each one with a <see cref="VoiceReply"/> carrying the same id.</summary>
public abstract record VoiceRequest(long Id) : VoiceMessage;

/// <summary>Worker → app, answer to the <see cref="VoiceRequest"/> with the same id; <see cref="ErrorReply"/> if it failed.</summary>
public abstract record VoiceReply(long Id) : VoiceMessage;

/// <summary>Heartbeat (H1.1).</summary>
public sealed record PingRequest(long Id) : VoiceRequest(Id);

public sealed record PongReply(long Id) : VoiceReply(Id);

/// <summary>Active capture devices (H1.2).</summary>
public sealed record ListMicrophonesRequest(long Id) : VoiceRequest(Id);

public sealed record MicrophonesReply(long Id, IReadOnlyList<MicrophoneInfo> Microphones) : VoiceReply(Id);

/// <param name="Id">Windows endpoint id: stable while the device stays plugged in the same port.</param>
/// <param name="Name">As Windows shows it, e.g. "Micrófono (Realtek(R) Audio)".</param>
/// <param name="IsDefault">Windows' default recording device right now.</param>
public sealed record MicrophoneInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// Opens the microphone and starts filling the in-memory buffer (16 kHz mono). A capture still open (a start the app
/// gave up on) is closed and its audio discarded first, so the microphone is never left open by a lost reply.
/// </summary>
/// <param name="MicrophoneId">A <see cref="MicrophoneInfo.Id"/>, or null for Windows' default device at this moment.</param>
public sealed record StartCaptureRequest(long Id, string? MicrophoneId) : VoiceRequest(Id);

public sealed record CaptureStartedReply(long Id, string MicrophoneName) : VoiceReply(Id);

/// <summary>Closes the microphone. The audio stays in the worker (H2 transcribes it there).</summary>
public sealed record StopCaptureRequest(long Id) : VoiceRequest(Id);

/// <param name="DurationMs">Audio actually captured, from the sample count.</param>
/// <param name="PeakLevel">Loudest sample, 0–1: near 0 means the microphone heard nothing.</param>
/// <param name="Truncated">The buffer filled up (<c>MaxCaptureSeconds</c>) and later audio was dropped.</param>
/// <param name="MicrophoneLost">The device went away (unplugged, driver reset) before the stop: only the audio up to
/// that moment was captured.</param>
public sealed record CaptureStoppedReply(long Id, double DurationMs, double PeakLevel, bool Truncated, bool MicrophoneLost = false)
    : VoiceReply(Id);

/// <summary>
/// Loads the speech model (V0.2 H1.3) and warms it up with a short silent decode, so the first dictation does not pay
/// the CUDA kernel start. Answered at once when that model is already loaded; a second request while it loads waits for
/// the same load. The worker keeps answering pings while it loads. There is no unload: freeing the model inside the
/// process leaves the CUDA context (~70 MiB of VRAM) and ~700 MB of RAM, so TextFlow ends the worker instead.
/// </summary>
/// <param name="ModelPath">The ggml model file.</param>
/// <param name="GpuPackPath">Folder of the GPU pack (ADR-0009 §H0.5, holds <c>runtimes\cuda12\win-x64</c>), or null
/// to use only what ships next to the worker.</param>
public sealed record LoadModelRequest(long Id, string ModelPath, string? GpuPackPath) : VoiceRequest(Id);

/// <param name="Runtime">The native runtime Whisper.net loaded (<c>Cuda12</c>).</param>
/// <param name="LoadMs">Reading the model into the GPU; 0 when <paramref name="WasLoaded"/>.</param>
/// <param name="WarmupMs">The silent warm-up decode; 0 when <paramref name="WasLoaded"/>.</param>
/// <param name="WasLoaded">The model was already loaded: nothing was done.</param>
public sealed record ModelLoadedReply(long Id, string Runtime, double LoadMs, double WarmupMs, bool WasLoaded) : VoiceReply(Id);

public sealed record ErrorReply(long Id, VoiceErrorCode Code) : VoiceReply(Id);

[JsonConverter(typeof(JsonStringEnumConverter<VoiceErrorCode>))]
public enum VoiceErrorCode
{
    Unknown,
    /// <summary>The worker does not know this request (app and worker from different builds).</summary>
    UnknownRequest,
    /// <summary>No active recording device at all.</summary>
    NoMicrophone,
    /// <summary>The requested device is unplugged or disabled.</summary>
    MicrophoneNotFound,
    /// <summary>Windows privacy settings block microphone access for desktop apps.</summary>
    MicrophoneAccessDenied,
    /// <summary>The device went away (unplugged, driver reset) as it was being opened.</summary>
    MicrophoneLost,
    NotCapturing,
    /// <summary>The model file is not there (not downloaded yet, or deleted).</summary>
    ModelNotFound,
    /// <summary>whisper.cpp could not read the model (corrupt file, out of memory).</summary>
    ModelLoadFailed,
    /// <summary>No usable NVIDIA GPU: only the CPU runtime loaded, and turbo on CPU takes 23–27 s per phrase (ADR-0009).</summary>
    NoGpu,
}

public static class VoiceProtocol
{
    /// <summary>Bumped on any incompatible change; app and worker ship together, so a mismatch means a broken install.</summary>
    public const int Version = 3;

    public const string PipeArgument = "--pipe";

    public const string ParentArgument = "--parent";

    /// <summary>Whisper's input format, produced by Windows' own converter (H1.2).</summary>
    public const int SampleRate = 16000;

    /// <summary>Longest single dictation kept; later audio is dropped and reported as truncated.</summary>
    public const int MaxCaptureSeconds = 60;
}
