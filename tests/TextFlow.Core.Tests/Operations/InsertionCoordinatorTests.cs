using TextFlow.Contracts.Insertion;
using TextFlow.Contracts.Targeting;
using TextFlow.Core.Operations;

namespace TextFlow.Core.Tests.Operations;

public class InsertionCoordinatorTests
{
    private static readonly ActiveTarget Target = new(
        1, 2, 100, 200, "notepad.exe", "Notepad", "t",
        new MonitorInfo(3, "D1", 96, 96, new PixelRect(0, 0, 10, 10)),
        FocusedControlInfo.Unknown, IsElevated: false, DateTimeOffset.UnixEpoch);

    private readonly FakeResolver _resolver = new();
    private readonly FakeStrategy _clipboard = new(InsertionStrategyKind.Clipboard);
    private readonly FakeStrategy _sendInput = new(InsertionStrategyKind.SendInput);

    private InsertionCoordinator Coordinator(int sendInputMaxLength = 16) =>
        new(_resolver, [_clipboard, _sendInput], new InsertionOptions(sendInputMaxLength));

    private static InsertionRequest Request(string text, InsertionStrategyKind? preferred = null) =>
        new(Target, text, 0, preferred);

    [Fact]
    public async Task InvalidTarget_ReturnsTargetChanged_AndNeverCallsStrategies()
    {
        _resolver.Validation = new TargetValidation(TargetValidationStatus.NotForeground, "x");

        var result = await Coordinator().InsertAsync(Request("hola"), CancellationToken.None);

        Assert.Equal(InsertionStatus.TargetChanged, result.Status);
        Assert.Equal(0, _clipboard.Calls + _sendInput.Calls);
    }

    [Fact]
    public async Task ElevatedTarget_ReturnsPermissionDenied()
    {
        var request = Request("hola") with { Target = Target with { IsElevated = true } };

        var result = await Coordinator().InsertAsync(request, CancellationToken.None);

        Assert.Equal(InsertionStatus.PermissionDenied, result.Status);
        Assert.Equal(0, _clipboard.Calls + _sendInput.Calls);
    }

    [Fact]
    public async Task ShortSingleLineText_UsesSendInputFirst()
    {
        var result = await Coordinator().InsertAsync(Request("hola"), CancellationToken.None);

        Assert.Equal(InsertionStrategyKind.SendInput, result.Strategy);
        Assert.Equal(0, _clipboard.Calls);
    }

    [Fact]
    public async Task DefaultOptions_UseClipboardFirst_EvenForShortText()
    {
        var coordinator = new InsertionCoordinator(_resolver, [_clipboard, _sendInput], new InsertionOptions());

        var result = await coordinator.InsertAsync(Request("hola"), CancellationToken.None);

        Assert.Equal(InsertionStrategyKind.Clipboard, result.Strategy);
        Assert.Equal(0, _sendInput.Calls);
    }

    [Fact]
    public async Task LongText_UsesClipboardFirst()
    {
        var result = await Coordinator().InsertAsync(Request(new string('a', 100)), CancellationToken.None);

        Assert.Equal(InsertionStrategyKind.Clipboard, result.Strategy);
    }

    [Fact]
    public async Task MultiLineShortText_UsesClipboardFirst()
    {
        var result = await Coordinator().InsertAsync(Request("a\r\nb"), CancellationToken.None);

        Assert.Equal(InsertionStrategyKind.Clipboard, result.Strategy);
    }

    [Fact]
    public async Task PreferredStrategy_IsTriedFirst()
    {
        var result = await Coordinator().InsertAsync(Request("hola", InsertionStrategyKind.Clipboard), CancellationToken.None);

        Assert.Equal(InsertionStrategyKind.Clipboard, result.Strategy);
    }

    [Fact]
    public async Task FailureBeforeAnyInput_FallsBackToNextStrategy()
    {
        _clipboard.Next = new InsertionResult(InsertionStatus.ClipboardConflict, InsertionStrategyKind.Clipboard, TimeSpan.Zero, InputSent: false);

        var result = await Coordinator().InsertAsync(Request(new string('a', 100)), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(InsertionStrategyKind.SendInput, result.Strategy);
    }

    [Fact]
    public async Task FailureAfterInputSent_DoesNotFallBack_ToAvoidDuplicateText()
    {
        _clipboard.Next = new InsertionResult(InsertionStatus.Failed, InsertionStrategyKind.Clipboard, TimeSpan.Zero, InputSent: true);

        var result = await Coordinator().InsertAsync(Request(new string('a', 100)), CancellationToken.None);

        Assert.Equal(InsertionStatus.Failed, result.Status);
        Assert.Equal(0, _sendInput.Calls);
    }

    [Fact]
    public async Task TargetIsRevalidatedBeforeFallback()
    {
        _clipboard.Next = new InsertionResult(InsertionStatus.UnsupportedTarget, InsertionStrategyKind.Clipboard, TimeSpan.Zero);
        _clipboard.OnInsert = () => _resolver.Validation = new TargetValidation(TargetValidationStatus.FocusChanged, "x");

        var result = await Coordinator().InsertAsync(Request(new string('a', 100)), CancellationToken.None);

        Assert.Equal(InsertionStatus.TargetChanged, result.Status);
        Assert.Equal(0, _sendInput.Calls);
    }

    [Fact]
    public async Task StrategiesThatCannotHandle_AreSkipped()
    {
        _sendInput.Handles = false;

        var result = await Coordinator().InsertAsync(Request("hola"), CancellationToken.None);

        Assert.Equal(InsertionStrategyKind.Clipboard, result.Strategy);
    }

    [Fact]
    public async Task NoUsableStrategy_ReturnsUnsupportedTarget()
    {
        _sendInput.Handles = false;
        _clipboard.Handles = false;

        var result = await Coordinator().InsertAsync(Request("hola"), CancellationToken.None);

        Assert.Equal(InsertionStatus.UnsupportedTarget, result.Status);
    }

    [Fact]
    public async Task CancelledToken_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Coordinator().InsertAsync(Request("hola"), cts.Token);

        Assert.Equal(InsertionStatus.Cancelled, result.Status);
    }

    private sealed class FakeResolver : ITargetResolver
    {
        public TargetValidation Validation { get; set; } = TargetValidation.Valid;

        public ActiveTarget? CaptureTarget() => Target;

        public TargetValidation ValidateTarget(ActiveTarget target) => Validation;

        public bool Activate(ActiveTarget target) => true;
    }

    private sealed class FakeStrategy(InsertionStrategyKind kind) : IInsertionStrategy
    {
        public InsertionStrategyKind Kind => kind;

        public bool Handles { get; set; } = true;

        public int Calls { get; private set; }

        public InsertionResult? Next { get; set; }

        public Action? OnInsert { get; set; }

        public bool CanHandle(InsertionRequest request) => Handles;

        public Task<InsertionResult> InsertAsync(InsertionRequest request, CancellationToken ct)
        {
            Calls++;
            OnInsert?.Invoke();
            return Task.FromResult(Next ?? new InsertionResult(InsertionStatus.Success, kind, TimeSpan.Zero, InputSent: true));
        }
    }
}
