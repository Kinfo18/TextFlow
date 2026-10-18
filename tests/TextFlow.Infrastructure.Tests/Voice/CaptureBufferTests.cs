using System.Buffers.Binary;
using TextFlow.VoiceWorker.Audio;

namespace TextFlow.Infrastructure.Tests.Voice;

public class CaptureBufferTests
{
    [Fact]
    public void Samples_AreKept_WithDurationAndPeak()
    {
        var buffer = new CaptureBuffer(capacitySamples: 32000);

        buffer.WritePcm16(Pcm(0, 1000, -16384, 200));
        buffer.WritePcm16(Pcm(5, 6));

        Assert.Equal(6, buffer.Count);
        Assert.Equal([0, 1000, -16384, 200, 5, 6], buffer.ToArray());
        Assert.Equal(0.5, buffer.PeakLevel);
        Assert.Equal(6 * 1000.0 / 16000, buffer.DurationMs);
        Assert.False(buffer.Truncated);
    }

    [Fact]
    public void OneSecondAt16kHz_Is1000Ms()
    {
        var buffer = new CaptureBuffer();

        buffer.WritePcm16(new byte[16000 * 2]);

        Assert.Equal(1000, buffer.DurationMs);
    }

    [Fact]
    public void FullestNegativeSample_IsPeakOne()
    {
        var buffer = new CaptureBuffer(10);

        buffer.WritePcm16(Pcm(short.MinValue));

        Assert.Equal(1.0, buffer.PeakLevel);
    }

    [Fact]
    public void WhenFull_LaterAudioIsDropped_AndMarkedTruncated()
    {
        var buffer = new CaptureBuffer(capacitySamples: 3);

        buffer.WritePcm16(Pcm(1, 2));
        buffer.WritePcm16(Pcm(3, 4, 5));

        Assert.Equal([1, 2, 3], buffer.ToArray());
        Assert.True(buffer.Truncated);
    }

    [Fact]
    public void TrailingOddByte_IsIgnored()
    {
        var buffer = new CaptureBuffer(10);

        buffer.WritePcm16([.. Pcm(7), 0xFF]);

        Assert.Equal([7], buffer.ToArray());
    }

    [Fact]
    public void Clear_ZeroesTheAudio_AndResets()
    {
        var buffer = new CaptureBuffer(capacitySamples: 2);
        buffer.WritePcm16(Pcm(100, 200, 300));

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.PeakLevel);
        Assert.False(buffer.Truncated);
        buffer.WritePcm16(Pcm(9));
        Assert.Equal([9], buffer.ToArray());
    }

    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        }

        return bytes;
    }
}
