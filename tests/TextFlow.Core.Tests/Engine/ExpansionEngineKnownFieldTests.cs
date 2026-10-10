using TextFlow.Core.Input;

namespace TextFlow.Core.Tests.Engine;

/// <summary>
/// Win11 Notepad bounces the focus between two of its windows right after every paste, and browsers fire focus
/// events while the user types. Turning capture off for each re-evaluation lost the keys typed meanwhile (half of a
/// long abbreviation never matched). A focus event on a field already allowed in this window keeps capturing; the
/// engine re-checks it quietly.
/// </summary>
public sealed partial class ExpansionEngineTests
{
    [Fact]
    public async Task FocusBackOnAKnownField_KeepsCapturing_AndRechecksThePolicy()
    {
        await StartAsync();
        Assert.True(_hook.CaptureEnabled);
        var captures = _resolver.Captures;
        bool? onWhileChecking = null;
        _resolver.DuringCapture = () => onWhileChecking = _hook.CaptureEnabled;

        await TypeAsync(new FocusChanged(FakeResolver.Window, KnownField: true));

        Assert.True(onWhileChecking); // no gap: keys typed during the check still reach the matcher
        Assert.True(_hook.CaptureEnabled);
        Assert.Equal(captures + 1, _resolver.Captures);
        Assert.Equal(0, _hook.KnownFieldsForgotten);
    }

    [Fact]
    public async Task KnownFieldThatIsNowAPasswordField_StopsCapture_AndForgetsTheKnownFields()
    {
        await StartAsync();
        _resolver.Target = FakeResolver.Notepad with { Control = FakeResolver.Notepad.Control with { IsPassword = true } };

        await TypeAsync(new FocusChanged(FakeResolver.Window, KnownField: true));

        Assert.False(_hook.CaptureEnabled);
        Assert.Equal(1, _hook.KnownFieldsForgotten);
    }

    [Fact]
    public async Task KnownFieldRecheckWithoutAnAnswer_KeepsCapturing()
    {
        await StartAsync();
        _resolver.Target = null; // UI Automation did not answer in time

        await TypeAsync(new FocusChanged(FakeResolver.Window, KnownField: true));

        Assert.True(_hook.CaptureEnabled);
    }

    [Fact]
    public async Task KnownFieldWhilePaused_DoesNotTurnCaptureOn()
    {
        await StartAsync();
        _engine.Pause();
        await SettleAsync();

        await TypeAsync(new FocusChanged(FakeResolver.Window, KnownField: true));

        Assert.False(_hook.CaptureEnabled);
    }

    [Fact]
    public async Task UnknownFieldQueuedAfterAKnownOne_IsStillEvaluated()
    {
        await StartAsync();
        _hook.Raise(new FocusChanged(FakeResolver.Window, KnownField: true));
        _hook.RaiseFocusChange(new FocusChanged(FakeResolver.Window));
        await SettleAsync();

        Assert.True(_hook.CaptureEnabled);
    }
}
