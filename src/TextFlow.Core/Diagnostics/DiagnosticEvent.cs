using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;

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
    InsertionStrategyKind Strategy,
    InsertionStatus Status,
    double ElapsedMs,
    bool FromMenu,
    bool SoundPlayed) : DiagnosticEvent(At);

public sealed record MenuShown(DateTimeOffset At, [property: SafeToLog] string TargetProcess, bool AnchoredToCaret) : DiagnosticEvent(At);

public sealed record MenuClosed(DateTimeOffset At, MenuCloseReason Reason) : DiagnosticEvent(At);

public sealed record TargetRejected(DateTimeOffset At, [property: SafeToLog] string TargetProcess, TargetValidationStatus Status) : DiagnosticEvent(At);

public sealed record HookReinstalled(DateTimeOffset At, int TimesThisSession) : DiagnosticEvent(At);

public sealed record EngineStateChanged(DateTimeOffset At, EngineState State) : DiagnosticEvent(At);

public enum MenuCloseReason
{
    Chosen,
    Escape,
    OtherKey,
    ClickOutside,
    FocusChanged,
    LongerTrigger,
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
