using TextFlow.Core.Diagnostics;

namespace TextFlow.Core.Voice;

public enum VoiceWorkerState
{
    Stopped,
    Starting,
    Ready,
    /// <summary>The worker ended unexpectedly; waiting before the next start.</summary>
    Restarting,
    /// <summary>Too many crashes in a row: no more restarts until <see cref="VoiceWorkerSupervisor.Start"/> is called again.</summary>
    Faulted,
}

public enum VoiceWorkerExitReason
{
    /// <summary>TextFlow asked it to stop.</summary>
    Stopped,
    /// <summary>The process ended or closed its pipe on its own.</summary>
    Crashed,
    /// <summary>It stopped answering pings and was killed.</summary>
    Hung,
    /// <summary>The process could not be started or never connected to the pipe.</summary>
    LaunchFailed,
    /// <summary>It connected but did not answer the hello in time, or speaks another protocol version.</summary>
    HandshakeFailed,
    /// <summary>It sent something that is not a valid message.</summary>
    ProtocolError,
}

/// <summary>The voice worker is up and answered the handshake.</summary>
public sealed record VoiceWorkerStarted(DateTimeOffset At, int ProcessId, double StartMs) : DiagnosticEvent(At);

/// <param name="ExitCode">Null when the process could not be started or its exit code was not known in time.</param>
public sealed record VoiceWorkerExited(DateTimeOffset At, VoiceWorkerExitReason Reason, int? ExitCode, double UptimeSeconds)
    : DiagnosticEvent(At);

/// <summary>The supervisor gave up after <paramref name="Crashes"/> crashes in a short time.</summary>
public sealed record VoiceWorkerFaulted(DateTimeOffset At, int Crashes) : DiagnosticEvent(At);

/// <summary>The voice worker is not running or went away while a request was waiting for it.</summary>
public sealed class VoiceWorkerUnavailableException : Exception
{
    public VoiceWorkerUnavailableException()
        : base("The voice worker is not available.")
    {
    }

    public VoiceWorkerUnavailableException(string message)
        : base(message)
    {
    }

    public VoiceWorkerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
