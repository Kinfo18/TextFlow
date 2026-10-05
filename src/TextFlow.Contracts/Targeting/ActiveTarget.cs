namespace TextFlow.Contracts.Targeting;

/// <summary>
/// Identity of the window/control an operation was started against.
/// The focus is an entity, never a screen coordinate (spec §3.4).
/// </summary>
/// <remarks>
/// <see cref="WindowTitle"/> may contain user content (document names, e-mail subjects).
/// It is kept in memory for exclusion rules only and must never be logged or persisted.
/// </remarks>
public sealed record ActiveTarget(
    nint WindowHandle,
    nint FocusHandle,
    int ProcessId,
    int ThreadId,
    string ProcessName,
    string WindowClass,
    string WindowTitle,
    MonitorInfo Monitor,
    FocusedControlInfo Control,
    bool IsElevated,
    DateTimeOffset CapturedAt);

public sealed record MonitorInfo(
    nint Handle,
    string DeviceName,
    uint DpiX,
    uint DpiY,
    PixelRect Bounds)
{
    public double Scale => DpiX / 96.0;
}

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>Metadata about the focused element. Never contains its text value.</summary>
/// <param name="ElementId">UI Automation runtime id of the focused element, when known: tells two fields of the same
/// window apart (browser tabs share one HWND). Opaque, content-free.</param>
public sealed record FocusedControlInfo(
    string ControlType,
    string ClassName,
    string FrameworkId,
    bool IsPassword,
    bool IsReadOnly,
    PixelRect? CaretBounds,
    string? ElementId = null)
{
    public static FocusedControlInfo Unknown { get; } =
        new("Unknown", string.Empty, string.Empty, IsPassword: false, IsReadOnly: false, CaretBounds: null);
}
