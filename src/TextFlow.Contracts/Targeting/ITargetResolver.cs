namespace TextFlow.Contracts.Targeting;

public interface ITargetResolver
{
    /// <summary>Captures the current foreground window and focused control. Null when nothing usable is focused.</summary>
    ActiveTarget? CaptureTarget();

    /// <summary>
    /// <see cref="CaptureTarget()"/>, optionally without the UI Automation caret lookup (Chrome &amp; co.): only menus and
    /// field prompts are placed at the caret, and the lookup costs time on every plain expansion.
    /// </summary>
    ActiveTarget? CaptureTarget(bool locateCaret) => CaptureTarget();

    /// <summary>
    /// <see cref="CaptureTarget(bool)"/> without the caret, giving UI Automation more time to say which element has the
    /// focus: used to recognise the field a template was typed in, where no keystroke waits for the answer and a busy
    /// web page may need longer than a typing-time check allows.
    /// </summary>
    ActiveTarget? CaptureTargetPatiently() => CaptureTarget(locateCaret: false);

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

    /// <summary>
    /// The text selected in the target's focused field ({{selection}}), read through UI Automation without touching
    /// the clipboard. Null when nothing is selected or the app does not expose it. Never logged or stored.
    /// </summary>
    string? ReadSelectedText(ActiveTarget target) => null;

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
