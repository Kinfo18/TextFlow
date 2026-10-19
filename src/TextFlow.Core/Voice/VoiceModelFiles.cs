namespace TextFlow.Core.Voice;

/// <summary>Where the speech model and the GPU pack live (ADR-0009 §H0.5); H1.4 downloads them there.</summary>
/// <param name="ModelPath">The ggml model file.</param>
/// <param name="GpuPackPath">The unpacked GPU pack (holds <c>runtimes\cuda12\win-x64</c>), or null to use only the CPU
/// runtime that ships with the worker (the load then fails with <c>NoGpu</c>).</param>
public sealed record VoiceModelFiles(string ModelPath, string? GpuPackPath)
{
    public const string ModelFileName = "ggml-large-v3-turbo-q5_0.bin";

    /// <summary>Version 1 of the GPU pack: Whisper.net 1.9.1 CUDA + CUDA 12.4 runtime and cuBLAS.</summary>
    public const string GpuPackFolder = "cuda12-1";

    /// <summary>Under the data folder (<c>%LOCALAPPDATA%\TextFlow</c>), outside Velopack's app folder.</summary>
    public static VoiceModelFiles In(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        return new VoiceModelFiles(
            Path.Combine(dataDirectory, "models", ModelFileName),
            Path.Combine(dataDirectory, "voz", GpuPackFolder));
    }
}
