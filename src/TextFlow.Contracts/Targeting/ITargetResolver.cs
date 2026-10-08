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

    /// <summary>
    /// Remembers the target's focused field and caret (UI Automation, content-free) so <see cref="RestoreField"/> can
    /// return to it after the user clicked elsewhere in the same window, e.g. to copy a value from the same web page.
    /// </summary>
    void BookmarkField(ActiveTarget target)
    {
    }

    /// <summary>Gives the focus and caret back to the field bookmarked for <paramref name="target"/>.</summary>
    /// <returns>False when nothing was bookmarked for it or the field refused (gone, re-rendered).</returns>
    bool RestoreField(ActiveTarget target) => false;
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
