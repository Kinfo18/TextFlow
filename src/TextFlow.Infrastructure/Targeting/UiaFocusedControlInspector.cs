using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Security;

namespace TextFlow.Infrastructure.Targeting;

public interface IFocusedControlInspector
{
    /// <summary>Returns metadata of the focused element if it belongs to <paramref name="expectedProcessId"/>.</summary>
    FocusedControlInfo? Inspect(uint expectedProcessId);

    /// <summary>Caret rectangle of the focused text in <paramref name="expectedProcessId"/>, from UIA text ranges (H4.4).</summary>
    PixelRect? LocateCaret(uint expectedProcessId);
}

/// <summary>
/// Reads focused-element metadata through UIA3. Never reads the element's value (its label is read only to spot
/// password fields and is not kept).
/// UIA calls into the target process and can hang on unresponsive apps, so every call is time-boxed.
/// </summary>
public sealed class UiaFocusedControlInspector : IFocusedControlInspector, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(150);

    private readonly UIA3Automation _automation;
    private readonly TimeSpan _timeout;

    public UiaFocusedControlInspector(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _automation = new UIA3Automation
        {
            ConnectionTimeout = _timeout,
            TransactionTimeout = _timeout,
        };
    }

    public FocusedControlInfo? Inspect(uint expectedProcessId) => TimeBoxed(() => InspectCore(expectedProcessId));

    /// <summary>Separate call with its own time box, so a slow text provider never costs the password check.</summary>
    public PixelRect? LocateCaret(uint expectedProcessId) =>
        TimeBoxed(() => FocusedElementOf(expectedProcessId) is { } element ? UiaCaretReader.Read(element) : null);

    private T? TimeBoxed<T>(Func<T?> read)
    {
        var task = Task.Run(read);
        try
        {
            return task.Wait(_timeout) ? task.Result : default;
        }
        catch (AggregateException)
        {
            return default; // UIA provider errors are expected on some apps; metadata is best-effort.
        }
    }

    private AutomationElement? FocusedElementOf(uint expectedProcessId)
    {
        var element = _automation.FocusedElement();
        return element is not null && element.Properties.ProcessId.TryGetValue(out var pid) && pid == expectedProcessId
            ? element
            : null; // focus moved to another process between Win32 and UIA reads
    }

    private FocusedControlInfo? InspectCore(uint expectedProcessId)
    {
        var element = FocusedElementOf(expectedProcessId);
        if (element is null)
        {
            return null;
        }

        var props = element.Properties;
        return new FocusedControlInfo(
            ControlType: props.ControlType.ValueOrDefault.ToString(),
            ClassName: props.ClassName.ValueOrDefault ?? string.Empty,
            FrameworkId: props.FrameworkId.ValueOrDefault ?? string.Empty,
            // "Show password" turns the field into plain text: its label/id still give it away (labels, not values).
            IsPassword: props.IsPassword.ValueOrDefault
                || SensitiveFieldHeuristic.LooksLikePassword(props.Name.ValueOrDefault, props.AutomationId.ValueOrDefault),
            IsReadOnly: IsReadOnly(element),
            CaretBounds: null,
            ElementId: RuntimeId(element));
    }

    /// <summary>UIA runtime id ("42.1234.4.567"): stable while the element lives, different for another tab or field.</summary>
    private static string? RuntimeId(AutomationElement element) =>
        element.Properties.RuntimeId.TryGetValue(out var id) && id is { Length: > 0 } ? string.Join('.', id) : null;

    private static bool IsReadOnly(AutomationElement element) =>
        element.Patterns.Value.TryGetPattern(out var value) && value.IsReadOnly.ValueOrDefault;

    public void Dispose() => _automation.Dispose();
}
