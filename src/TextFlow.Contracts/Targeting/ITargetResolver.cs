namespace TextFlow.Contracts.Targeting;

public interface ITargetResolver
{
    /// <summary>Captures the current foreground window and focused control. Null when nothing usable is focused.</summary>
    ActiveTarget? CaptureTarget();

    /// <summary>Checks whether a previously captured target is still the same entity and can receive input.</summary>
    TargetValidation ValidateTarget(ActiveTarget target);

    /// <summary>Brings the target's window back to the foreground (after TextFlow's own field prompt had the focus).</summary>
    /// <returns>False if Windows refused or the window is gone.</returns>
    bool Activate(ActiveTarget target);
}

public enum TargetValidationStatus
{
    Valid,
    WindowGone,
    ProcessChanged,
    NotForeground,
    FocusChanged,
    Elevated,
}

public sealed record TargetValidation(TargetValidationStatus Status, string Reason)
{
    public bool IsValid => Status == TargetValidationStatus.Valid;

    public static TargetValidation Valid { get; } = new(TargetValidationStatus.Valid, string.Empty);
}
