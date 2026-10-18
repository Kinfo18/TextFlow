using TextFlow.Contracts.Voice;

namespace TextFlow.VoiceWorker.Audio;

/// <summary>The recording devices (WASAPI in the worker, fakes in tests).</summary>
internal interface IMicrophones
{
    /// <exception cref="MicrophoneException">Windows could not list them.</exception>
    IReadOnlyList<MicrophoneInfo> List();

    /// <summary>Opens a device (null: Windows' default now) and fills <paramref name="buffer"/> until stopped.</summary>
    /// <exception cref="MicrophoneException">No device, not found, access denied.</exception>
    IMicrophoneCapture Start(string? microphoneId, CaptureBuffer buffer);
}

/// <summary>An open microphone.</summary>
internal interface IMicrophoneCapture : IDisposable
{
    string Name { get; }

    /// <summary>Closes the device after its last audio is in the buffer.</summary>
    /// <returns>False if the device went away while recording: the buffer holds the audio up to that moment.</returns>
    bool Stop();
}

internal sealed class MicrophoneException : Exception
{
    public MicrophoneException()
    {
    }

    public MicrophoneException(VoiceErrorCode code, Exception? innerException = null)
        : base($"Microphone error: {code}.", innerException)
    {
        Code = code;
    }

    public MicrophoneException(string message)
        : base(message)
    {
    }

    public MicrophoneException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VoiceErrorCode Code { get; } = VoiceErrorCode.Unknown;
}
