namespace TextFlow.Core.Voice;

/// <summary>Starts a <c>TextFlow.VoiceWorker</c> process and connects to it (the Windows side lives in Infrastructure).</summary>
public interface IVoiceWorkerLauncher
{
    /// <summary>Returns once the worker's pipe is connected; the protocol handshake is the supervisor's job.</summary>
    Task<IVoiceWorkerConnection> LaunchAsync(CancellationToken cancellationToken);
}

/// <summary>One running worker: its pipe and its process.</summary>
public interface IVoiceWorkerConnection : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>Duplex pipe to the worker, framed with <see cref="Contracts.Voice.VoiceFrame"/>.</summary>
    Stream Stream { get; }

    /// <summary>Completes with the exit code when the process ends, for whatever reason.</summary>
    Task<int> Exited { get; }

    /// <summary>Ends the process at once (hung worker, or it ignored <see cref="Contracts.Voice.ShutdownMessage"/>).</summary>
    void Kill();
}
