using TextFlow.Contracts.Targeting;

namespace TextFlow.Contracts.Insertion;

public interface ITextInsertionService
{
    Task<InsertionResult> InsertAsync(InsertionRequest request, CancellationToken ct);
}

/// <summary>A single insertion strategy (clipboard, SendInput, ...). Selected by capabilities (ADR-0001).</summary>
public interface IInsertionStrategy
{
    InsertionStrategyKind Kind { get; }

    bool CanHandle(InsertionRequest request);

    Task<InsertionResult> InsertAsync(InsertionRequest request, CancellationToken ct);
}

public enum InsertionStrategyKind
{
    Clipboard,
    SendInput,
    UIAutomation,
    Tsf,
}

/// <param name="Text">Text to insert. In-memory only; never log.</param>
/// <param name="BackspacesBefore">Characters to delete before inserting (e.g. the typed trigger).</param>
/// <param name="CaretOffsetFromEnd">Left-arrow presses after inserting, to place the caret at <c>{{cursor}}</c>.</param>
/// <param name="Delivered">Invoked at most once, on any thread, the moment the text reached the target (paste or typing
/// sent), before slower cleanup such as restoring the clipboard. Lets the chime match what the user sees.</param>
/// <param name="PressEnterAfter">Presses Enter once the text is in, to send a chat message.</param>
public sealed record InsertionRequest(
    ActiveTarget Target,
    string Text,
    int BackspacesBefore = 0,
    InsertionStrategyKind? PreferredStrategy = null,
    int CaretOffsetFromEnd = 0,
    Action? Delivered = null,
    bool PressEnterAfter = false);

public enum InsertionStatus
{
    Success,
    UnsupportedTarget,
    TargetChanged,
    PermissionDenied,
    ForegroundRequired,
    ClipboardConflict,
    Failed,
    Cancelled,
}

/// <summary>Content-free outcome of an insertion. Safe to log.</summary>
/// <param name="InputSent">True once any keystroke reached the target; a fallback strategy would then duplicate text.</param>
public sealed record InsertionResult(
    InsertionStatus Status,
    InsertionStrategyKind? Strategy,
    TimeSpan Elapsed,
    string? Detail = null,
    bool InputSent = false)
{
    public bool Succeeded => Status == InsertionStatus.Success;
}
