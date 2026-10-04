using System.Diagnostics;
using TextFlow.Contracts.Insertion;
using TextFlow.Infrastructure.Input;
using TextFlow.Infrastructure.Windows;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextFlow.Infrastructure.Clipboard;

/// <param name="PasteSettleDelay">Time the target gets to read the clipboard before it is restored.
/// Electron/Chromium apps read asynchronously; restoring too early pastes the old content.</param>
public sealed record ClipboardStrategyOptions(TimeSpan PasteSettleDelay)
{
    public static ClipboardStrategyOptions Default { get; } = new(TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// Transactional paste: snapshot → private text → Ctrl+V → settle → restore unless someone else
/// changed the clipboard meanwhile (then the user's newer copy wins and we report ClipboardConflict).
/// </summary>
public sealed class ClipboardStrategy : IInsertionStrategy, IDisposable
{
    private static readonly TimeSpan ModifierTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly HWND MessageOnlyParent = new(-3); // HWND_MESSAGE

    private readonly ClipboardStrategyOptions _options;
    private readonly MessageLoopThread _thread;
    private readonly HWND _owner;
    private readonly ClipboardStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClipboardStrategy(ClipboardStrategyOptions? options = null)
    {
        _options = options ?? ClipboardStrategyOptions.Default;
        _thread = new MessageLoopThread("TextFlow.Clipboard");
        _owner = _thread.InvokeAsync(CreateOwnerWindow).GetAwaiter().GetResult();
        if (_owner.IsNull)
        {
            throw new InvalidOperationException("Could not create clipboard owner window.");
        }

        _store = new ClipboardStore(_owner);
    }

    public InsertionStrategyKind Kind => InsertionStrategyKind.Clipboard;

    public bool CanHandle(InsertionRequest request) => true;

    public async Task<InsertionResult> InsertAsync(InsertionRequest request, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        ClipboardSnapshot? snapshot = null;
        try
        {
            if (!await KeyboardInput.WaitForModifiersReleasedAsync(ModifierTimeout, ct).ConfigureAwait(false))
            {
                return Result(InsertionStatus.Failed, clock, "Modifier keys still held.", inputSent: false);
            }

            snapshot = await _thread.InvokeAsync(_store.TakeSnapshot).ConfigureAwait(false);
            if (snapshot is null)
            {
                return Result(InsertionStatus.ClipboardConflict, clock, "Clipboard busy or too large to preserve.", inputSent: false);
            }

            if (!await _thread.InvokeAsync(() => _store.SetPrivateText(request.Text)).ConfigureAwait(false))
            {
                await _thread.InvokeAsync(() => _store.Restore(snapshot)).ConfigureAwait(false);
                return Result(InsertionStatus.ClipboardConflict, clock, "Could not write clipboard.", inputSent: false);
            }

            var ourSequence = ClipboardStore.SequenceNumber;
            var sentBack = KeyboardInput.Tap(VIRTUAL_KEY.VK_BACK, request.BackspacesBefore);
            if (sentBack != request.BackspacesBefore * 2)
            {
                // Pasting after a partial delete would leave trigger remnants in the target.
                await _thread.InvokeAsync(() => _store.Restore(snapshot)).ConfigureAwait(false);
                return Result(InsertionStatus.Failed, clock, "Backspace injection incomplete.", inputSent: sentBack > 0);
            }

            var pasted = KeyboardInput.Chord(VIRTUAL_KEY.VK_CONTROL, VIRTUAL_KEY.VK_V) == 4;
            var inputSent = sentBack > 0 || pasted;
            if (pasted)
            {
                request.Delivered?.Invoke();
            }

            // Not cancellable: once Ctrl+V was sent we must still try to restore the user's clipboard.
            await Task.Delay(_options.PasteSettleDelay, CancellationToken.None).ConfigureAwait(false);
            KeyboardInput.Tap(VIRTUAL_KEY.VK_LEFT, request.CaretOffsetFromEnd);

            if (ClipboardStore.SequenceNumber != ourSequence)
            {
                return Result(InsertionStatus.ClipboardConflict, clock, "Clipboard changed during paste; not restored.", inputSent);
            }

            var restored = await _thread.InvokeAsync(() => _store.Restore(snapshot)).ConfigureAwait(false);
            if (!pasted)
            {
                return Result(InsertionStatus.Failed, clock, "Paste injection blocked.", inputSent);
            }

            return Result(InsertionStatus.Success, clock, restored ? null : "Clipboard restore failed.", inputSent);
        }
        finally
        {
            snapshot?.Wipe();
            _gate.Release();
        }
    }

    private InsertionResult Result(InsertionStatus status, Stopwatch clock, string? detail, bool inputSent) =>
        new(status, Kind, clock.Elapsed, detail, inputSent);

    private static unsafe HWND CreateOwnerWindow()
    {
        fixed (char* className = "STATIC")
        {
            return PInvoke.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, MessageOnlyParent, default, default, null);
        }
    }

    public void Dispose()
    {
        _thread.InvokeAsync(() => PInvoke.DestroyWindow(_owner)).Wait(TimeSpan.FromSeconds(1));
        _thread.Dispose();
        _gate.Dispose();
    }
}
