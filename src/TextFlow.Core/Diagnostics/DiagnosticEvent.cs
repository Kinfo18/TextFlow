using TextFlow.Contracts.Insertion;

namespace TextFlow.Core.Diagnostics;

/// <summary>
/// Marks a string field of a <see cref="DiagnosticEvent"/> as metadata, never user content (process names,
/// strategy names). Allowed names are pinned by a privacy test; do not use it for text, titles or triggers.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class SafeToLogAttribute : Attribute;

/// <summary>
/// Content-free technical events (spec §20). Fields may only be numbers, enums, times or <see cref="SafeToLogAttribute"/>
/// strings: a privacy test rejects anything else, so snippet text, typed buffers and window titles cannot leak.
/// </summary>
public abstract record DiagnosticEvent(DateTimeOffset At);

public sealed record ExpansionCompleted(
    DateTimeOffset At,
    [property: SafeToLog] string TargetProcess,
    InsertionStrategyKind? Strategy,
    InsertionStatus Status,
    double ElapsedMs,
    bool FromMenu,
    bool SoundPlayed) : DiagnosticEvent(At);

public sealed record MenuShown(DateTimeOffset At, [property: SafeToLog] string TargetProcess, bool AnchoredToCaret) : DiagnosticEvent(At);

public sealed record MenuClosed(DateTimeOffset At, MenuCloseReason Reason) : DiagnosticEvent(At);

public sealed record TargetRejected(DateTimeOffset At, [property: SafeToLog] string TargetProcess, RejectionReason Reason) : DiagnosticEvent(At);

/// <param name="MissedInputMs">Input the hook never saw, in ms: large values mean a real removal.</param>
/// <param name="MaxCallbackMs">Slowest hook callback so far: near LowLevelHooksTimeout explains why Windows removed it.</param>
/// <param name="TargetProcess">Foreground app at the time (process name only).</param>
public sealed record HookReinstalled(
    DateTimeOffset At,
    int TimesThisSession,
    int MissedInputMs = 0,
    double MaxCallbackMs = 0,
    [property: SafeToLog] string TargetProcess = "") : DiagnosticEvent(At);

public sealed record EngineStateChanged(DateTimeOffset At, EngineState State) : DiagnosticEvent(At);

/// <summary>A library was installed in the engine (startup, file changed, user picked another backup).</summary>
public sealed record LibraryLoaded(DateTimeOffset At, int Menus, int Commands, int DirectTriggers, int Issues) : DiagnosticEvent(At);

/// <summary>An event failed and was skipped; the engine keeps running. Only the exception type, never its message.</summary>
public sealed record EngineFault(DateTimeOffset At, [property: SafeToLog] string ExceptionType) : DiagnosticEvent(At);

public enum MenuCloseReason
{
    Chosen,
    Escape,
    OtherKey,
    ClickOutside,
    FocusChanged,
    LongerTrigger,
}

public enum RejectionReason
{
    NoTarget,
    WindowChanged,
    PolicyDenied,
}

public enum EngineState
{
    Starting,
    Running,
    Paused,
    Stopped,
}

/// <summary>Where diagnostic events go (file, memory for the Diagnostics page, tests).</summary>
public interface IDiagnosticSink
{
    void Record(DiagnosticEvent diagnostic);
}
