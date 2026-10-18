using System.Buffers.Binary;
using TextFlow.Contracts.Voice;

namespace TextFlow.VoiceWorker.Audio;

/// <summary>
/// One dictation's audio in memory, 16 kHz mono PCM16 (V0.2 H1.2). Fixed size: once full, later audio is dropped and
/// <see cref="Truncated"/> is set. Never written to disk (ADR-0006); <see cref="Clear"/> zeroes it. Written from the
/// capture thread, read from the worker loop.
/// </summary>
internal sealed class CaptureBuffer
{
    private readonly short[] _samples;
    private readonly Lock _gate = new();
    private int _count;
    private int _peak;
    private bool _truncated;

    public CaptureBuffer(int capacitySamples = VoiceProtocol.SampleRate * VoiceProtocol.MaxCaptureSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacitySamples);
        _samples = new short[capacitySamples];
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public double DurationMs => Count * 1000.0 / VoiceProtocol.SampleRate;

    /// <summary>Loudest sample so far, 0–1.</summary>
    public double PeakLevel
    {
        get
        {
            lock (_gate)
            {
                return _peak / 32768.0;
            }
        }
    }

    public bool Truncated
    {
        get
        {
            lock (_gate)
            {
                return _truncated;
            }
        }
    }

    /// <summary>Appends little-endian 16-bit samples; a trailing odd byte (never sent by WASAPI) is ignored.</summary>
    public void WritePcm16(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            var incoming = pcm.Length / 2;
            var room = _samples.Length - _count;
            if (incoming > room)
            {
                _truncated = true;
                incoming = room;
            }

            for (var i = 0; i < incoming; i++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));
                _samples[_count + i] = sample;
                var magnitude = Math.Abs((int)sample);
                if (magnitude > _peak)
                {
                    _peak = magnitude;
                }
            }

            _count += incoming;
        }
    }

    /// <summary>A copy of the captured samples (H2 hands them to whisper); the caller zeroes it when done (ADR-0006).</summary>
    public short[] ToArray()
    {
        lock (_gate)
        {
            return _samples.AsSpan(0, _count).ToArray();
        }
    }

    /// <summary>Zeroes the audio and starts over.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_samples, 0, _count);
            _count = 0;
            _peak = 0;
            _truncated = false;
        }
    }
}
