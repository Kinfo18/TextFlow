using System.Runtime.InteropServices;
using TextFlow.Contracts.Targeting;
using TextFlow.Infrastructure.Targeting;

namespace TextFlow.Infrastructure.Tests.Desktop;

/// <summary>A WinForms window with a multiline TextBox running on its own STA UI thread.</summary>
internal sealed partial class TestWindow : IDisposable
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);

    private readonly Form _form;
    private readonly TextBox _textBox;
    private readonly Thread _thread;

    private TestWindow(Form form, TextBox textBox, Thread thread)
    {
        _form = form;
        _textBox = textBox;
        _thread = thread;
    }

    public bool IsForeground => GetForegroundWindow() == _form.Handle;

    public static async Task<TestWindow> OpenAsync(string initialText = "")
    {
        var ready = new TaskCompletionSource<TestWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var textBox = new TextBox { Multiline = true, Dock = DockStyle.Fill, Text = initialText, AcceptsTab = true };
            var form = new Form { Text = "TextFlow desktop test", Width = 500, Height = 300, TopMost = true, StartPosition = FormStartPosition.CenterScreen };
            form.Controls.Add(textBox);
            form.Shown += (_, _) =>
            {
                ForceForeground(form.Handle);
                textBox.Focus();
                textBox.SelectionStart = textBox.TextLength;
                ready.SetResult(new TestWindow(form, textBox, Thread.CurrentThread));
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        var window = await ready.Task;
        await Task.Delay(SettleDelay);
        return window;
    }

    public ActiveTarget CaptureTarget()
    {
        var target = new Win32TargetResolver(new UiaFocusedControlInspector()).CaptureTarget();
        return target is not null && target.WindowHandle == _form.Handle
            ? target
            : throw new InvalidOperationException("Foreground target is not the test window.");
    }

    public Task<string> GetTextAsync() => OnUiAsync(() => _textBox.Text);

    public Task SetClipboardTextAsync(string text) => OnUiAsync(() =>
    {
        System.Windows.Forms.Clipboard.SetText(text);
        return true;
    });

    public Task<string> GetClipboardTextAsync() => OnUiAsync(System.Windows.Forms.Clipboard.GetText);

    private Task<T> OnUiAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _form.BeginInvoke(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>Foreground lock workaround: attach to the current foreground thread's input queue.</summary>
    private static void ForceForeground(nint hwnd)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, 0);
        var ownThread = GetCurrentThreadId();
        var attached = foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(ownThread, foregroundThread, false);
            }
        }
    }

    public void Dispose()
    {
        if (_form.IsHandleCreated)
        {
            _form.BeginInvoke(_form.Close);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, nint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);
}
