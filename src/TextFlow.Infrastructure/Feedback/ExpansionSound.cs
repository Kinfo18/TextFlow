using System.Diagnostics;
using TextFlow.Core.Engine;
using TextFlow.Core.Feedback;
using Windows.Win32;
using Windows.Win32.Media.Audio;

namespace TextFlow.Infrastructure.Feedback;

/// <summary>
/// Plays the expansion chime asynchronously from memory (winmm PlaySound, SND_MEMORY | SND_ASYNC).
/// The WAV buffer is pinned because PlaySound keeps reading it after returning; changing the volume
/// stops any playing chime before the old buffer is released. When the audio device may still be waking (first sound
/// after a pause) the chime with the longer lead-in plays, otherwise the instant one (see <see cref="AudioWakeTracker"/>).
/// </summary>
public sealed class ExpansionSound : IExpansionFeedback
{
    /// <summary>Long enough for slow endpoints to wake (the chime was lost after ~10 s idle on the dev laptop).</summary>
    private static readonly TimeSpan WakeLength = TimeSpan.FromMilliseconds(400);

    /// <summary>A sound within this window already keeps the endpoint awake; never cut a chime short with noise.</summary>
    private static readonly TimeSpan WarmCooldown = TimeSpan.FromSeconds(3);

    /// <summary>Conservative: the dev laptop's endpoint slept after ~10 s idle.</summary>
    private static readonly TimeSpan SleepsAfter = TimeSpan.FromSeconds(8);

    private static readonly byte[] WakeNoise = Pin(ChimeSynth.CreateWakeNoise(WakeLength));

    private readonly Lock _gate = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly AudioWakeTracker _wake = new(SleepsAfter, WakeLength);
    private byte[] _chime;
    private byte[] _coldChime;
    private double _volume;
    private long _lastSoundAt;

    /// <param name="volume">0-1, see <see cref="Volume"/>.</param>
    public ExpansionSound(double volume = 1.0)
    {
        _volume = Math.Clamp(volume, 0, 1);
        _chime = Pin(ChimeSynth.CreateExpansionChime(_volume));
        _coldChime = Pin(ChimeSynth.CreateExpansionChime(_volume, ChimeSynth.ColdLeadIn));
    }

    /// <summary>User preference; off means <see cref="Play"/> does nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>User volume 0-1 (settings slider), independent of the Windows mixer.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            var volume = Math.Clamp(value, 0, 1);
            var chime = Pin(ChimeSynth.CreateExpansionChime(volume));
            var coldChime = Pin(ChimeSynth.CreateExpansionChime(volume, ChimeSynth.ColdLeadIn));
            lock (_gate)
            {
                Stop();
                _volume = volume;
                _chime = chime;
                _coldChime = coldChime;
            }
        }
    }

    /// <returns>False if sound is disabled or Windows refused to play (no audio device).</returns>
    public unsafe bool Play()
    {
        if (!Enabled || _volume <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            var wavBuffer = _wake.IsAwake(Now) ? _chime : _coldChime;
            MarkSoundStarted();
            fixed (byte* wav = wavBuffer)
            {
                return PInvoke.PlaySound(
                    (char*)wav,
                    default,
                    SND_FLAGS.SND_MEMORY | SND_FLAGS.SND_ASYNC | SND_FLAGS.SND_NODEFAULT);
            }
        }
    }

    /// <summary>Plays inaudible noise unless a sound played very recently (see <see cref="WarmCooldown"/>).</summary>
    public unsafe void Warm()
    {
        if (!Enabled || _volume <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_lastSoundAt != 0 && Stopwatch.GetElapsedTime(_lastSoundAt) < WarmCooldown)
            {
                return;
            }

            MarkSoundStarted();
            fixed (byte* wav = WakeNoise)
            {
                PInvoke.PlaySound((char*)wav, default, SND_FLAGS.SND_MEMORY | SND_FLAGS.SND_ASYNC | SND_FLAGS.SND_NODEFAULT);
            }
        }
    }

    private TimeSpan Now => Stopwatch.GetElapsedTime(_origin);

    private void MarkSoundStarted()
    {
        _lastSoundAt = Stopwatch.GetTimestamp();
        _wake.SoundStarted(Now);
    }

    private static unsafe void Stop() => PInvoke.PlaySound((char*)null, default, 0);

    private static byte[] Pin(byte[] wav)
    {
        var pinned = GC.AllocateArray<byte>(wav.Length, pinned: true);
        wav.CopyTo(pinned, 0);
        return pinned;
    }
}
