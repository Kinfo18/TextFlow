using TextFlow.Contracts.Voice;

namespace TextFlow.VoiceWorker.Speech;

/// <summary>Loads the speech model (Whisper.net in the worker, fakes in tests).</summary>
internal interface ISpeechModels
{
    /// <summary>Reads the model into memory and warms it up. Slow (0.5–3 s): never called on the pipe loop.</summary>
    /// <exception cref="SpeechModelException">Missing file, unreadable model, no GPU.</exception>
    ILoadedSpeechModel Load(string modelPath, string? gpuPackPath);
}

/// <summary>A model ready to transcribe.</summary>
internal interface ILoadedSpeechModel : IDisposable
{
    /// <summary>The native runtime in use (<c>Cuda12</c>).</summary>
    string Runtime { get; }

    double LoadMs { get; }

    double WarmupMs { get; }
}

internal sealed class SpeechModelException : Exception
{
    public SpeechModelException()
    {
    }

    public SpeechModelException(VoiceErrorCode code, Exception? innerException = null)
        : base($"Speech model error: {code}.", innerException)
    {
        Code = code;
    }

    public SpeechModelException(string message)
        : base(message)
    {
    }

    public SpeechModelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VoiceErrorCode Code { get; } = VoiceErrorCode.ModelLoadFailed;
}
