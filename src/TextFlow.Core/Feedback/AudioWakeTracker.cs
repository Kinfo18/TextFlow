namespace TextFlow.Core.Feedback;

/// <summary>
/// Guesses whether the audio device is awake: laptop endpoints power down after some seconds without sound and need
/// a moment to wake, swallowing whatever plays meanwhile. Lets the chime be instant when the device is awake and keep
/// a longer inaudible lead-in when it may still be waking (first expansion after a pause, H4.2). Not thread-safe.
/// </summary>
/// <param name="sleepsAfter">Silence, measured from the last sound's start, after which the device may be asleep.</param>
/// <param name="wakeTime">How long the first sound after a silence keeps the device busy waking up.</param>
public sealed class AudioWakeTracker(TimeSpan sleepsAfter, TimeSpan wakeTime)
{
    private TimeSpan? _lastSound;
    private TimeSpan _awakeFrom;

    /// <param name="now">Monotonic time (any fixed origin).</param>
    public void SoundStarted(TimeSpan now)
    {
        if (_lastSound is not { } last || now - last > sleepsAfter)
        {
            _awakeFrom = now + wakeTime;
        }

        _lastSound = now;
    }

    public bool IsAwake(TimeSpan now) => _lastSound is { } last && now - last <= sleepsAfter && now >= _awakeFrom;
}
