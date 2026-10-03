using System.Buffers.Binary;
using TextFlow.Core.Feedback;

namespace TextFlow.Core.Tests.Feedback;

public class ChimeSynthTests
{
    private static readonly byte[] Wav = ChimeSynth.CreateExpansionChime();

    [Fact]
    public void Chime_IsRiffWave_PcmMono16Bit()
    {
        Assert.Equal("RIFF"u8.ToArray(), Wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), Wav[8..12]);
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(Wav.AsSpan(20))); // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(Wav.AsSpan(22))); // mono
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(Wav.AsSpan(34)));
    }

    [Fact]
    public void Chime_HeaderSizes_MatchPayload()
    {
        Assert.Equal(Wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(Wav.AsSpan(4)));
        Assert.Equal(Wav.Length - 44, BinaryPrimitives.ReadInt32LittleEndian(Wav.AsSpan(40)));
    }

    private static short[] Samples(byte[] wav) => Enumerable.Range(0, (wav.Length - 44) / 2)
        .Select(i => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (i * 2))))
        .ToArray();

    [Fact]
    public void Chime_IsShort_IncludingLeadIn()
    {
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(Wav.AsSpan(24));
        var duration = TimeSpan.FromSeconds((Wav.Length - 44) / 2.0 / sampleRate);

        Assert.InRange(duration.TotalMilliseconds, 180, 260);
    }

    [Fact]
    public void Chime_StartsWithFaintNonSilentLeadIn_SoIdleAudioDevicesWakeBeforeTheNotes()
    {
        var leadIn = Samples(Wav).Take((int)(44_100 * ChimeSynth.LeadIn.TotalSeconds)).ToArray();

        Assert.Contains(leadIn, s => s != 0);                     // not digital silence: some drivers skip it
        Assert.All(leadIn, s => Assert.InRange(s, -8, 8));         // inaudible (≈ -72 dBFS)
    }

    [Fact]
    public void WakeNoise_IsInaudibleButNotSilent_ForTheRequestedDuration()
    {
        var wav = ChimeSynth.CreateWakeNoise(TimeSpan.FromMilliseconds(400));
        var samples = Samples(wav);

        Assert.Equal(44_100 * 400 / 1000, samples.Length);
        Assert.Contains(samples, s => s != 0);
        Assert.All(samples, s => Assert.InRange(s, -8, 8));
    }

    [Fact]
    public void Chime_IsSoft_AndEndsInSilence()
    {
        var samples = Enumerable.Range(0, (Wav.Length - 44) / 2)
            .Select(i => BinaryPrimitives.ReadInt16LittleEndian(Wav.AsSpan(44 + (i * 2))))
            .ToArray();

        Assert.True(samples.Max(s => Math.Abs((int)s)) < short.MaxValue / 2, "peak must stay below -6 dBFS");
        Assert.True(Math.Abs((int)samples[^1]) < 100, "no click at the end");
        Assert.True(Math.Abs((int)samples[0]) < 100, "no click at the start");
    }

    [Fact]
    public void Chime_IsDeterministic()
    {
        Assert.Equal(Wav, ChimeSynth.CreateExpansionChime());
    }

    private static int PeakOf(byte[] wav) => Enumerable.Range(0, (wav.Length - 44) / 2)
        .Max(i => Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + (i * 2)))));

    [Fact]
    public void Volume_ScalesPeakLinearly()
    {
        var full = PeakOf(ChimeSynth.CreateExpansionChime(1.0));
        var half = PeakOf(ChimeSynth.CreateExpansionChime(0.5));

        Assert.InRange(half, (full / 2) - 2, (full / 2) + 2);
    }

    [Fact]
    public void Volume_Zero_IsSilent()
    {
        Assert.Equal(0, PeakOf(ChimeSynth.CreateExpansionChime(0)));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    public void Volume_IsClampedToZeroOne(double requested, double effective)
    {
        Assert.Equal(ChimeSynth.CreateExpansionChime(effective), ChimeSynth.CreateExpansionChime(requested));
    }

    [Fact]
    public void Volume_DefaultIsFull()
    {
        Assert.Equal(ChimeSynth.CreateExpansionChime(1.0), Wav);
    }
}
