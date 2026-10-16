using TextFlow.Core.Input;

namespace TextFlow.Core.Tests.Input;

public sealed class HoldToTalkTests
{
    private const uint RightCtrl = 0xA3;
    private const uint LeftCtrl = 0xA2;
    private const uint RightAlt = 0xA5;
    private const uint KeyC = 'C';

    private readonly HoldToTalk _hold = new(RightCtrl);

    private static KeyStroke Down(uint vk, long at, uint scan = 0, bool injected = false) => new(vk, scan, true, injected, at);

    private static KeyStroke Up(uint vk, long at, uint scan = 0, bool injected = false) => new(vk, scan, false, injected, at);

    [Fact]
    public void HoldingTheKeyPastTheThreshold_StartsDictation()
    {
        Assert.Equal(HoldResult.None, _hold.Feed(Down(RightCtrl, 1_000)));

        Assert.Equal(HoldResult.None, _hold.Tick(1_149));
        Assert.Equal(HoldResult.Started, _hold.Tick(1_150));
        Assert.True(_hold.IsTalking);
    }

    [Fact]
    public void ReleasingAfterStart_StopsDictation()
    {
        _hold.Feed(Down(RightCtrl, 1_000));
        _hold.Tick(1_200);

        Assert.Equal(HoldResult.Stopped, _hold.Feed(Up(RightCtrl, 3_000)));
        Assert.False(_hold.IsTalking);
    }

    [Fact]
    public void ShortTap_DoesNothing()
    {
        _hold.Feed(Down(RightCtrl, 1_000));

        Assert.Equal(HoldResult.None, _hold.Feed(Up(RightCtrl, 1_100)));
        Assert.Equal(HoldResult.None, _hold.Tick(2_000));
    }

    [Fact]
    public void AutoRepeatPastTheThreshold_StartsWithoutWaitingForTheTimer()
    {
        _hold.Feed(Down(RightCtrl, 1_000));

        Assert.Equal(HoldResult.Started, _hold.Feed(Down(RightCtrl, 1_160)));
        Assert.Equal(HoldResult.None, _hold.Feed(Down(RightCtrl, 1_190)));
    }

    /// <summary>Right Ctrl + C is a copy, not dictation: the shortcut wins and the hold never starts.</summary>
    [Fact]
    public void OtherKeyBeforeTheThreshold_IsAShortcut_AndNeverStarts()
    {
        _hold.Feed(Down(RightCtrl, 1_000));
        _hold.Feed(Down(KeyC, 1_050));

        Assert.Equal(HoldResult.None, _hold.Tick(1_500));
        Assert.Equal(HoldResult.None, _hold.Feed(Up(RightCtrl, 1_600)));
        Assert.False(_hold.IsTalking);
    }

    /// <summary>Holding Ctrl while thinking, then pressing a key: the user wanted a shortcut, so nothing is inserted.</summary>
    [Fact]
    public void OtherKeyWhileTalking_CancelsDictation()
    {
        _hold.Feed(Down(RightCtrl, 1_000));
        _hold.Tick(1_200);

        Assert.Equal(HoldResult.Cancelled, _hold.Feed(Down(KeyC, 2_000)));
        Assert.False(_hold.IsTalking);
        Assert.Equal(HoldResult.None, _hold.Feed(Up(RightCtrl, 2_100)));
    }

    [Fact]
    public void ReleasingOtherKeys_IsIgnored()
    {
        _hold.Feed(Down(RightCtrl, 1_000));

        Assert.Equal(HoldResult.None, _hold.Feed(Up(KeyC, 1_050)));
        Assert.Equal(HoldResult.Started, _hold.Tick(1_150));
    }

    /// <summary>TextFlow's own SendInput (Ctrl+V on paste) must never start or cancel dictation.</summary>
    [Fact]
    public void InjectedKeys_AreIgnored()
    {
        Assert.Equal(HoldResult.None, _hold.Feed(Down(RightCtrl, 1_000, injected: true)));
        Assert.Equal(HoldResult.None, _hold.Tick(2_000));

        _hold.Feed(Down(RightCtrl, 3_000));
        _hold.Tick(3_200);
        Assert.Equal(HoldResult.None, _hold.Feed(Down('V', 3_300, injected: true)));
        Assert.True(_hold.IsTalking);
    }

    /// <summary>AltGr on a Spanish layout sends a fake Left Ctrl (scan 0x21D) plus Right Alt: never Right Ctrl.</summary>
    [Fact]
    public void AltGr_DoesNotStartRightCtrlDictation()
    {
        _hold.Feed(Down(LeftCtrl, 1_000, scan: 0x21D));
        _hold.Feed(Down(RightAlt, 1_000, scan: 0x38));

        Assert.Equal(HoldResult.None, _hold.Tick(2_000));
        Assert.False(_hold.IsTalking);
    }

    [Fact]
    public void Interrupt_BeforeStart_IsAShortcut()
    {
        _hold.Feed(Down(RightCtrl, 1_000));

        _hold.Interrupt(); // Ctrl+click: multi-select, not dictation

        Assert.Equal(HoldResult.None, _hold.Tick(1_500));
    }

    [Fact]
    public void Interrupt_WhileTalking_KeepsTalking()
    {
        _hold.Feed(Down(RightCtrl, 1_000));
        _hold.Tick(1_200);

        _hold.Interrupt();

        Assert.True(_hold.IsTalking);
        Assert.Equal(HoldResult.Stopped, _hold.Feed(Up(RightCtrl, 2_000)));
    }

    [Fact]
    public void CanStartAgainAfterAShortcut()
    {
        _hold.Feed(Down(RightCtrl, 1_000));
        _hold.Feed(Down(KeyC, 1_050));
        _hold.Feed(Up(KeyC, 1_080));
        _hold.Feed(Up(RightCtrl, 1_100));

        _hold.Feed(Down(RightCtrl, 2_000));
        Assert.Equal(HoldResult.Started, _hold.Tick(2_150));
    }

    /// <summary>The user's "short tap" measured 415 ms (H0.4 probe): recording already started, but nothing is inserted.</summary>
    [Fact]
    public void ReleaseBeforeTheMinimumHold_IsDiscarded()
    {
        _hold.Feed(Down(RightCtrl, 30_974));
        Assert.Equal(HoldResult.Started, _hold.Tick(31_124));

        Assert.Equal(HoldResult.Discarded, _hold.Feed(Up(RightCtrl, 31_389)));
        Assert.False(_hold.IsTalking);
    }

    [Fact]
    public void ReleaseAtTheMinimumHold_Stops()
    {
        _hold.Feed(Down(RightCtrl, 1_000));
        _hold.Tick(1_150);

        Assert.Equal(HoldResult.Stopped, _hold.Feed(Up(RightCtrl, 1_600)));
    }

    [Fact]
    public void ThresholdIsConfigurable()
    {
        var slow = new HoldToTalk(RightCtrl, holdMs: 300, minHoldMs: 1_000);
        slow.Feed(Down(RightCtrl, 1_000));

        Assert.Equal(HoldResult.None, slow.Tick(1_200));
        Assert.Equal(HoldResult.Started, slow.Tick(1_300));
        Assert.Equal(HoldResult.Discarded, slow.Feed(Up(RightCtrl, 1_900)));
    }
}
