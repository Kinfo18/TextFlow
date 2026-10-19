using System.Runtime.InteropServices;

namespace TextFlow.Infrastructure.Voice;

/// <summary>
/// Whether this PC can use the GPU pack (ADR-0009 §H0.5): an NVIDIA driver whose CUDA version is 12.0 or newer.
/// Asks <c>nvcuda.dll</c> (installed with every NVIDIA driver) for its version only, without <c>cuInit</c>: that would
/// start the CUDA driver inside TextFlow itself. Whether a GPU is really usable is known when the worker loads the
/// model (<c>NoGpu</c>).
/// </summary>
public static class NvidiaDriver
{
    private const int Cuda12 = 12000;

    /// <summary>The driver's CUDA version as 1000 × major + 10 × minor (12040 = 12.4); null without an NVIDIA driver.</summary>
    public static int? CudaVersion()
    {
        if (!NativeLibrary.TryLoad("nvcuda.dll", typeof(NvidiaDriver).Assembly, DllImportSearchPath.System32, out var library))
        {
            return null;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(library, "cuDriverGetVersion", out var export))
            {
                return null;
            }

            unsafe
            {
                var getVersion = (delegate* unmanaged<int*, int>)export;
                int version;
                return getVersion(&version) == 0 ? version : null;
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    public static bool SupportsCuda12() => CudaVersion() >= Cuda12;
}
