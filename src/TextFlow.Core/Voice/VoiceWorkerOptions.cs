namespace TextFlow.Core.Voice;

/// <summary>Timings of <see cref="VoiceWorkerSupervisor"/> (V0.2 H1.1); the defaults are the agreed design.</summary>
public sealed record VoiceWorkerOptions
{
    /// <summary>From a connected pipe to <see cref="Contracts.Voice.ReadyMessage"/>.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A ping not answered in this time means the worker hung (a stuck CUDA call, for instance): it is killed.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Longest wait for any other request (opening a microphone takes ~100 ms).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a stopping worker may take to exit on its own before it is killed.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Wait before the first restart; it doubles with every crash inside <see cref="CrashWindow"/>.</summary>
    public TimeSpan FirstRestartDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxRestartDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>This many crashes inside <see cref="CrashWindow"/> and the supervisor gives up (Faulted) until started again.</summary>
    public int MaxCrashes { get; init; } = 5;

    public TimeSpan CrashWindow { get; init; } = TimeSpan.FromMinutes(2);
}
