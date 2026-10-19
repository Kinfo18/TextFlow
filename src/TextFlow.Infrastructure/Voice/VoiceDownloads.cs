namespace TextFlow.Infrastructure.Voice;

/// <summary>The pinned origins of the voice files (ADR-0009 §H0.5): fixed revision, size and SHA-256, never "latest".</summary>
public static class VoiceDownloads
{
    /// <summary>whisper.cpp large-v3-turbo q5_0 from Hugging Face, revision 5359861 (MIT, OpenAI Whisper weights).</summary>
    public static readonly Uri ModelSource = new(
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-large-v3-turbo-q5_0.bin");

    public const long ModelSize = 574_041_195;

    public const string ModelSha256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2";

    /// <summary>The pack built by <c>.github/workflows/voz-gpu.yml</c> for the tag <c>voz-gpu-1</c>.</summary>
    public static readonly Uri GpuPackSource = new(
        "https://github.com/Kinfo18/TextFlow/releases/download/voz-gpu-1/textflow-voz-cuda12-1.zip");

    public const long GpuPackSize = 644_691_012;

    public const string GpuPackSha256 = "39bdab6453f63eda55ad7315200524815d32757e05d437253198e73db9130687";

    /// <summary>What the pack takes once unpacked (the DLL are ~1.1 GB, the zip ~620 MB).</summary>
    public const long GpuPackUnpackedSize = 1_150_000_000;
}
