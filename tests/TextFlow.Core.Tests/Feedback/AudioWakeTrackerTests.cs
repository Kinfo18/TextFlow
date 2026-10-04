using TextFlow.Core.Feedback;

namespace TextFlow.Core.Tests.Feedback;

public class AudioWakeTrackerTests
{
    private static readonly TimeSpan SleepsAfter = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan WakeTime = TimeSpan.FromMilliseconds(400);

    private static AudioWakeTracker Create() => new(SleepsAfter, WakeTime);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void BeforeAnySound_TheDeviceMayBeAsleep()
    {
        Assert.False(Create().IsAwake(Ms(5_000)));
    }

    [Fact]
    public void RightAfterTheFirstSound_TheDeviceIsStillWaking()
    {
        var tracker = Create();
        tracker.SoundStarted(Ms(1_000));

        Assert.False(tracker.IsAwake(Ms(1_150))); // a fast typist: trigger done 150 ms after the first key
    }

    [Fact]
    public void OnceTheWakeTimePassed_TheDeviceIsAwake()
    {
        var tracker = Create();
        tracker.SoundStarted(Ms(1_000));

        Assert.True(tracker.IsAwake(Ms(1_400)));
        Assert.True(tracker.IsAwake(Ms(8_900)));
    }

    [Fact]
    public void AfterALongSilence_TheDeviceIsAsleepAgain()
    {
        var tracker = Create();
        tracker.SoundStarted(Ms(1_000));

        Assert.False(tracker.IsAwake(Ms(9_100)));
    }

    [Fact]
    public void SoundsCloseTogether_KeepTheDeviceAwake_WithoutANewWakeTime()
    {
        var tracker = Create();
        tracker.SoundStarted(Ms(1_000));
        tracker.SoundStarted(Ms(4_000)); // warm noise while typing, device already awake

        Assert.True(tracker.IsAwake(Ms(4_050)));
        Assert.True(tracker.IsAwake(Ms(11_500)));
    }

    [Fact]
    public void ASoundAfterALongSilence_StartsANewWakeTime()
    {
        var tracker = Create();
        tracker.SoundStarted(Ms(1_000));
        tracker.SoundStarted(Ms(20_000));

        Assert.False(tracker.IsAwake(Ms(20_100)));
        Assert.True(tracker.IsAwake(Ms(20_400)));
    }
}
