using System.Diagnostics;
using TextFlow.Contracts.Insertion;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace TextFlow.Infrastructure.Input;

/// <summary>Types text with KEYEVENTF_UNICODE. Never touches the clipboard; slower for long text.</summary>
public sealed class SendInputStrategy : IInsertionStrategy
{
    private const int MaxLength = 2000;
    private static readonly TimeSpan ModifierTimeout = TimeSpan.FromMilliseconds(1500);

    public InsertionStrategyKind Kind => InsertionStrategyKind.SendInput;

    public bool CanHandle(InsertionRequest request) => request.Text.Length <= MaxLength;

    public async Task<InsertionResult> InsertAsync(InsertionRequest request, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        if (!await KeyboardInput.WaitForModifiersReleasedAsync(ModifierTimeout, ct).ConfigureAwait(false))
        {
            return new InsertionResult(InsertionStatus.Failed, Kind, clock.Elapsed, "Modifier keys still held.");
        }

        var sentBack = KeyboardInput.Tap(VIRTUAL_KEY.VK_BACK, request.BackspacesBefore);
        if (sentBack != request.BackspacesBefore * 2)
        {
            return new InsertionResult(InsertionStatus.Failed, Kind, clock.Elapsed, "Backspace injection blocked.", InputSent: sentBack > 0);
        }

        var (sent, expected) = KeyboardInput.TypeText(request.Text);
        if (sent != expected)
        {
            return new InsertionResult(InsertionStatus.Failed, Kind, clock.Elapsed, "Text injection incomplete.", InputSent: true);
        }

        KeyboardInput.Tap(VIRTUAL_KEY.VK_LEFT, request.CaretOffsetFromEnd);
        return new InsertionResult(InsertionStatus.Success, Kind, clock.Elapsed, InputSent: sent > 0 || sentBack > 0);
    }
}
