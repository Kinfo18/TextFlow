using System.Diagnostics;
using System.Runtime.InteropServices;
using TextFlow.Contracts.Voice;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace TextFlow.VoiceWorker.Speech;

/// <summary>
/// whisper.cpp through Whisper.net 1.9.1 (ADR-0009): CUDA 12 runtime from the GPU pack, flash attention on, refused when
/// only the CPU runtime loads. Whisper.net picks its native runtime once per process, at the first load.
/// </summary>
internal sealed class WhisperSpeechModels : ISpeechModels
{
    /// <summary>One second of silence: enough for the first decode to start the CUDA kernels.</summary>
    private static readonly float[] WarmupAudio = new float[VoiceProtocol.SampleRate];

    private readonly Lock _gate = new();
    private bool _runtimeChosen;

    public ILoadedSpeechModel Load(string modelPath, string? gpuPackPath)
    {
        if (!File.Exists(modelPath))
        {
            throw new SpeechModelException(VoiceErrorCode.ModelNotFound);
        }

        ChooseRuntime(gpuPackPath);

        var watch = Stopwatch.StartNew();
        WhisperFactory factory;
        try
        {
            // Without flash attention turbo filled the 4 GB RTX 2050 and took 5–12 s per phrase (ADR-0009).
            factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseFlashAttention = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SpeechModelException(VoiceErrorCode.ModelLoadFailed, ex);
        }

        var loadMs = watch.Elapsed.TotalMilliseconds;
        try
        {
            if (RuntimeOptions.LoadedLibrary is not RuntimeLibrary.Cuda12)
            {
                throw new SpeechModelException(VoiceErrorCode.NoGpu);
            }

            watch.Restart();
            Warmup(factory);
            return new LoadedModel(factory, RuntimeLibrary.Cuda12.ToString(), loadMs, watch.Elapsed.TotalMilliseconds);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Points Whisper.net at the GPU pack. It looks for <c>runtimes\cuda12\win-x64</c> next to the file named by
    /// <see cref="RuntimeOptions.LibraryPath"/>, but checks CUDA by loading <c>cudart64_12</c> by name, which Windows
    /// does not look for there: loaded by full path first, the check finds it already in the process.
    /// </summary>
    private void ChooseRuntime(string? gpuPackPath)
    {
        lock (_gate)
        {
            if (_runtimeChosen)
            {
                return;
            }

            _runtimeChosen = true;
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu];
            if (gpuPackPath is null)
            {
                return;
            }

            var cudart = Path.Combine(gpuPackPath, "runtimes", "cuda12", "win-x64", "cudart64_12.dll");
            if (NativeLibrary.TryLoad(cudart, out _))
            {
                RuntimeOptions.LibraryPath = Path.Combine(gpuPackPath, "whisper.dll");
            }
        }
    }

    private static void Warmup(WhisperFactory factory)
    {
        using var processor = factory.CreateBuilder().WithLanguage("es").Build();
        processor.Process(WarmupAudio); // only the decode matters: silence yields no text worth reading
    }

    private sealed class LoadedModel(WhisperFactory factory, string runtime, double loadMs, double warmupMs) : ILoadedSpeechModel
    {
        public string Runtime => runtime;

        public double LoadMs => loadMs;

        public double WarmupMs => warmupMs;

        public void Dispose() => factory.Dispose();
    }
}
