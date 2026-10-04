using System.Buffers.Binary;

namespace TextFlow.Core.Feedback;

/// <summary>
/// Synthesizes TextFlow's own expansion chime (no bundled or third-party audio): two short rising
/// notes with a soft attack and exponential decay, as a 16-bit mono PCM WAV.
/// </summary>
/// <remarks>
/// Laptop audio endpoints power down when idle and swallow the first ~100 ms on wake-up. Waking is the job of
/// <see cref="CreateWakeNoise"/>, played while the user types or browses a menu. When the device is known to be awake
/// the chime keeps only a short <see cref="LeadIn"/> of inaudible noise, because every lead-in millisecond is felt as
/// delay; when it may still be waking it uses <see cref="ColdLeadIn"/> instead (H4.2).
/// </remarks>
public static class ChimeSynth
{
    /// <summary>Lead-in for an awake device: the notes start almost at once.</summary>
    public static readonly TimeSpan LeadIn = TimeSpan.FromMilliseconds(15);

    /// <summary>Lead-in for a device that may still be waking (the length that played 12/12 in H1).</summary>
    public static readonly TimeSpan ColdLeadIn = TimeSpan.FromMilliseconds(120);

    private const int LeadInAmplitude = 6;
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
    /// <param name="leadIn">Inaudible noise before the notes; <see cref="LeadIn"/> when null.</param>
    public static byte[] CreateExpansionChime(double volume = 1.0, TimeSpan? leadIn = null)
    {
        var level = Math.Clamp(volume, 0, 1);
        var leadInSamples = (int)((leadIn ?? LeadIn).TotalSeconds * SampleRate);
        var samples = (level > 0 ? WakeUpNoise(leadInSamples) : Enumerable.Repeat((short)0, leadInSamples))
            .Concat(Notes.SelectMany(note => Tone(note.Frequency, note.Seconds, note.Gain * level)))
            .ToArray();
        return ToWav(samples);
    }

    /// <summary>
    /// Inaudible noise played while the user types, so a sleeping audio endpoint is already awake when the chime
    /// comes: on some laptops waking takes longer than <see cref="LeadIn"/> and the whole chime was lost.
    /// </summary>
    public static byte[] CreateWakeNoise(TimeSpan duration) =>
        ToWav(WakeUpNoise((int)(duration.TotalSeconds * SampleRate)).ToArray());

    private static byte[] ToWav(short[] samples)
    {
        var wav = new byte[HeaderBytes + (samples.Length * 2)];
        WriteHeader(wav, samples.Length * 2);

        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(HeaderBytes + (i * 2)), samples[i]);
        }

        return wav;
    }

    /// <summary>Deterministic ±6 LSB noise (≈ -72 dBFS): keeps the endpoint from treating it as silence.</summary>
    private static IEnumerable<short> WakeUpNoise(int count)
    {
        var state = 0x2545F491u;
        for (var i = 0; i < count; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            yield return (short)((int)(state % ((LeadInAmplitude * 2) + 1)) - LeadInAmplitude);
        }
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
