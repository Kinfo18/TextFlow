using System.Runtime.Versioning;
using TextFlow.Contracts.Targeting;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextFlow.Infrastructure.Targeting;

/// <summary>
/// Captures the foreground window + focused control as a stable identity (spec §8).
/// Win32 for identity (fast, never blocks); UI Automation only for control metadata, with a timeout.
/// </summary>
[SupportedOSPlatform("windows10.0.26100.0")]
public sealed unsafe class Win32TargetResolver : ITargetResolver
{
    private const int MaxClassNameLength = 256;
    private const int MaxTitleLength = 512;
    private const int MaxPathLength = 1024;

    private readonly IFocusedControlInspector _controlInspector;
    private readonly TimeProvider _time;

    public Win32TargetResolver(IFocusedControlInspector controlInspector, TimeProvider? time = null)
    {
        _controlInspector = controlInspector;
        _time = time ?? TimeProvider.System;
    }

    public ActiveTarget? CaptureTarget()
    {
        var foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull)
        {
            return null;
        }

        var root = PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOT);
        var window = root.IsNull ? foreground : root;
        var (threadId, processId) = GetThreadAndProcess(window);
        if (threadId == 0)
        {
            return null;
        }

        var (focus, caret) = GetFocusAndCaret(threadId);
        var processName = GetProcessName(processId);
        var control = _controlInspector.Inspect(processId) ?? FocusedControlInfo.Unknown;

        return new ActiveTarget(
            WindowHandle: (nint)window.Value,
            FocusHandle: focus,
            ProcessId: (int)processId,
            ThreadId: (int)threadId,
            ProcessName: processName,
            WindowClass: GetClassName(window),
            WindowTitle: GetWindowTitle(window),
            Monitor: GetMonitor(window),
            Control: caret is null ? control : control with { CaretBounds = caret },
            IsElevated: IsProcessElevated(processId),
            CapturedAt: _time.GetUtcNow());
    }

    public TargetValidation ValidateTarget(ActiveTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var window = new HWND((void*)target.WindowHandle);
        if (!PInvoke.IsWindow(window))
        {
            return new TargetValidation(TargetValidationStatus.WindowGone, "Window no longer exists.");
        }

        var (threadId, processId) = GetThreadAndProcess(window);
        if (processId != (uint)target.ProcessId)
        {
            // HWND values are recycled; a different owner means a different entity.
            return new TargetValidation(TargetValidationStatus.ProcessChanged, "Window handle now belongs to another process.");
        }

        var foreground = PInvoke.GetForegroundWindow();
        var foregroundRoot = foreground.IsNull ? foreground : PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (foregroundRoot != window)
        {
            return new TargetValidation(TargetValidationStatus.NotForeground, "Target window is not in the foreground.");
        }

        var (focus, _) = GetFocusAndCaret(threadId);
        if (target.FocusHandle != 0 && focus != target.FocusHandle)
        {
            return new TargetValidation(TargetValidationStatus.FocusChanged, "Focused control changed inside the target window.");
        }

        return TargetValidation.Valid;
    }

    private static (uint ThreadId, uint ProcessId) GetThreadAndProcess(HWND window)
    {
        uint processId;
        var threadId = PInvoke.GetWindowThreadProcessId(window, &processId);
        return (threadId, processId);
    }

    private static (nint Focus, PixelRect? Caret) GetFocusAndCaret(uint threadId)
    {
        var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        if (!PInvoke.GetGUIThreadInfo(threadId, &info))
        {
            return (0, null);
        }

        PixelRect? caret = null;
        if (!info.hwndCaret.IsNull)
        {
            var topLeft = new System.Drawing.Point(info.rcCaret.left, info.rcCaret.top);
            var bottomRight = new System.Drawing.Point(info.rcCaret.right, info.rcCaret.bottom);
            if (PInvoke.ClientToScreen(info.hwndCaret, &topLeft) && PInvoke.ClientToScreen(info.hwndCaret, &bottomRight))
            {
                caret = new PixelRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
            }
        }

        return ((nint)info.hwndFocus.Value, caret);
    }

    private static string GetClassName(HWND window)
    {
        var buffer = stackalloc char[MaxClassNameLength];
        var length = PInvoke.GetClassName(window, buffer, MaxClassNameLength);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static string GetWindowTitle(HWND window)
    {
        var buffer = stackalloc char[MaxTitleLength];
        var length = PInvoke.GetWindowText(window, buffer, MaxTitleLength);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static MonitorInfo GetMonitor(HWND window)
    {
        var monitor = PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEXW();
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        var bounds = default(PixelRect);
        var device = string.Empty;
        if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
        {
            var rc = info.monitorInfo.rcMonitor;
            bounds = new PixelRect(rc.left, rc.top, rc.right, rc.bottom);
            device = info.szDevice.ToString();
        }

        uint dpiX = 96, dpiY = 96;
        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY).Failed)
        {
            dpiX = dpiY = 96;
        }

        return new MonitorInfo((nint)monitor.Value, device, dpiX, dpiY, bounds);
    }

    private static string GetProcessName(uint processId)
    {
        using var process = OpenForQuery(processId);
        if (process.IsInvalid)
        {
            return string.Empty;
        }

        var buffer = stackalloc char[MaxPathLength];
        var size = (uint)MaxPathLength;
        if (!PInvoke.QueryFullProcessImageName((HANDLE)process.DangerousGetHandle(), PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &size))
        {
            return string.Empty;
        }

        return Path.GetFileName(new string(buffer, 0, (int)size));
    }

    /// <summary>Fails closed: if the token cannot be read, the target is treated as elevated.</summary>
    private static bool IsProcessElevated(uint processId)
    {
        using var process = OpenForQuery(processId);
        if (process.IsInvalid)
        {
            return true;
        }

        HANDLE token;
        if (!PInvoke.OpenProcessToken((HANDLE)process.DangerousGetHandle(), TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
        {
            return true;
        }

        try
        {
            TOKEN_ELEVATION elevation;
            uint returned;
            var ok = PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, &elevation, (uint)sizeof(TOKEN_ELEVATION), &returned);
            return !ok || elevation.TokenIsElevated != 0;
        }
        finally
        {
            PInvoke.CloseHandle(token);
        }
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenForQuery(uint processId)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        return new Microsoft.Win32.SafeHandles.SafeFileHandle((nint)handle.Value, ownsHandle: true);
    }
}
