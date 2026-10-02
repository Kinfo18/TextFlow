using System.Buffers.Binary;

namespace TextFlow.Core.Feedback;

/// <summary>
/// Synthesizes TextFlow's own expansion chime (no bundled or third-party audio): two short rising
/// notes with a soft attack and exponential decay, as a 16-bit mono PCM WAV.
/// </summary>
public static class ChimeSynth
{
    private const int SampleRate = 44_100;
    private const int HeaderBytes = 44;
    private const double Peak = 0.22; // ≈ -13 dBFS: audible but discreet
    private const double AttackSeconds = 0.004;

    private static readonly (double Frequency, double Seconds, double Gain)[] Notes =
    [
        (1318.5, 0.035, 0.8), // E6
        (1760.0, 0.060, 1.0), // A6
    ];

    /// <param name="volume">User volume 0-1 (clamped), linear on top of the built-in peak.</param>
    public static byte[] CreateExpansionChime(double volume = 1.0)
    {
        var level = Math.Clamp(volume, 0, 1);
        var samples = Notes.SelectMany(note => Tone(note.Frequency, note.Seconds, note.Gain * level)).ToArray();
        var wav = new byte[HeaderBytes + (samples.Length * 2)];
        WriteHeader(wav, samples.Length * 2);

        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(HeaderBytes + (i * 2)), samples[i]);
        }

        return wav;
    }

    private static IEnumerable<short> Tone(double frequency, double seconds, double gain)
    {
        var count = (int)(seconds * SampleRate);
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / SampleRate;
            var attack = Math.Min(1, t / AttackSeconds);
            var decay = Math.Exp(-5.5 * t / seconds);
            var tail = 1 - ((double)i / count); // reach exactly zero: no click between notes or at the end
            var value = Math.Sin(2 * Math.PI * frequency * t) * attack * decay * tail * gain * Peak;
            yield return (short)Math.Round(value * short.MaxValue);
        }
    }

    private static void WriteHeader(Span<byte> wav, int dataBytes)
    {
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav[4..], HeaderBytes - 8 + dataBytes);
        "WAVE"u8.CopyTo(wav[8..]);
        "fmt "u8.CopyTo(wav[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(wav[16..], 16);           // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(wav[20..], 1);            // PCM
        BinaryPrimitives.WriteInt16LittleEndian(wav[22..], 1);            // mono
        BinaryPrimitives.WriteInt32LittleEndian(wav[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav[28..], SampleRate * 2); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(wav[32..], 2);            // block align
        BinaryPrimitives.WriteInt16LittleEndian(wav[34..], 16);           // bits per sample
        "data"u8.CopyTo(wav[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(wav[40..], dataBytes);
    }
}
