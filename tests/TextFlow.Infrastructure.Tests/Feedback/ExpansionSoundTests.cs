using TextFlow.Infrastructure.Feedback;

namespace TextFlow.Infrastructure.Tests.Feedback;

/// <summary>Settings behaviour only: these cases never reach the audio device.</summary>
public sealed class ExpansionSoundTests
{
    [Fact]
    public void Play_WhenDisabled_PlaysNothing()
    {
        var sound = new ExpansionSound(0.5) { Enabled = false };

        Assert.False(sound.Play());
    }

    [Fact]
    public void Play_AtZeroVolume_PlaysNothing()
    {
        var sound = new ExpansionSound(0);

        Assert.False(sound.Play());
    }

    [Theory]
    [InlineData(-0.2, 0)]
    [InlineData(0.35, 0.35)]
    [InlineData(1.7, 1)]
    public void Volume_IsClampedToZeroOne(double requested, double expected)
    {
        var sound = new ExpansionSound { Volume = requested };

        Assert.Equal(expected, sound.Volume);
    }
}
