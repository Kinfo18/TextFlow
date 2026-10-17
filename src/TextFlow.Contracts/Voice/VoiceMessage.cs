using System.Text.Json.Serialization;

namespace TextFlow.Contracts.Voice;

/// <summary>
/// A message on the named pipe between TextFlow and <c>TextFlow.VoiceWorker</c> (V0.2 H1.1). Only control and text
/// cross the pipe: audio is captured inside the worker (spec §7). <see cref="VoiceFrame"/> carries one per frame.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(ReadyMessage), "ready")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(ShutdownMessage), "shutdown")]
public abstract record VoiceMessage;

/// <summary>App → worker, first message: the protocol the app speaks.</summary>
public sealed record HelloMessage(int ProtocolVersion) : VoiceMessage;

/// <summary>Worker → app, answer to <see cref="HelloMessage"/>: the worker is up and speaks <paramref name="ProtocolVersion"/>.</summary>
public sealed record ReadyMessage(int ProtocolVersion, int ProcessId) : VoiceMessage;

/// <summary>App → worker heartbeat; the worker answers with a <see cref="PongMessage"/> carrying the same id.</summary>
public sealed record PingMessage(long Id) : VoiceMessage;

public sealed record PongMessage(long Id) : VoiceMessage;

/// <summary>App → worker: finish and exit.</summary>
public sealed record ShutdownMessage : VoiceMessage;

public static class VoiceProtocol
{
    /// <summary>Bumped on any incompatible change; app and worker ship together, so a mismatch means a broken install.</summary>
    public const int Version = 1;

    public const string PipeArgument = "--pipe";

    public const string ParentArgument = "--parent";
}
