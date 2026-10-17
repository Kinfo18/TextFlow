using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using TextFlow.Contracts.Voice;

namespace TextFlow.VoiceWorker;

/// <summary>Exit codes of the worker; TextFlow logs them in <c>VoiceWorkerExited</c>.</summary>
internal static class WorkerExitCode
{
    public const int Ok = 0;
    public const int BadArguments = 2;
    public const int ConnectFailed = 3;
    public const int ProtocolError = 4;
    public const int PipeBroken = 5;
    public const int ParentGone = 6;
}

/// <summary>
/// Started by TextFlow as <c>TextFlow.VoiceWorker.exe --pipe NAME --parent PID</c>: connects to the pipe TextFlow
/// created and serves it until TextFlow says shutdown, closes the pipe, or its process disappears.
/// </summary>
internal static class WorkerHost
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out var pipeName, out var parentId))
        {
            return WorkerExitCode.BadArguments;
        }

        using var parentGone = new CancellationTokenSource();
        if (!WatchParent(parentId, parentGone))
        {
            return WorkerExitCode.ParentGone;
        }

        // CurrentUserOnly: refuses a pipe of that name created by another user.
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, parentGone.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return WorkerExitCode.ConnectFailed;
        }
        catch (OperationCanceledException)
        {
            return WorkerExitCode.ParentGone;
        }

        return await WorkerLoop.RunAsync(pipe, parentGone.Token).ConfigureAwait(false);
    }

    internal static bool TryParse(string[] args, out string pipeName, out int parentId)
    {
        pipeName = "";
        parentId = 0;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case VoiceProtocol.PipeArgument:
                    pipeName = args[i + 1];
                    break;
                case VoiceProtocol.ParentArgument:
                    _ = int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out parentId);
                    break;
                default:
                    return false;
            }
        }

        return pipeName.Length > 0 && parentId > 0;
    }

    /// <summary>Cancels <paramref name="parentGone"/> when TextFlow ends; false if it is already gone.</summary>
    private static bool WatchParent(int parentId, CancellationTokenSource parentGone)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(parentId);
        }
        catch (ArgumentException)
        {
            return false;
        }

        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) =>
        {
            try
            {
                parentGone.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // RunAsync already returned.
            }
        };
        return !parent.HasExited;
    }
}
