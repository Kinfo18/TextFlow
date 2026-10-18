using TextFlow.Contracts.Voice;
using TextFlow.VoiceWorker.Audio;

namespace TextFlow.Infrastructure.Tests.Voice;

/// <summary>
/// V0.2 H1.2 against the real default microphone: one second in memory must be one second of 16 kHz audio.
/// Skipped where there is no recording device (the CI runner). Only the length and level are checked; nothing is saved.
/// </summary>
public class WasapiMicrophonesTests
{
    private readonly WasapiMicrophones _microphones = new();

    [Fact]
    public void List_MarksAtMostOneDefault()
    {
        var all = _microphones.List();

        Assert.True(all.Count(m => m.IsDefault) <= 1);
        Assert.All(all, m => Assert.False(string.IsNullOrWhiteSpace(m.Id)));
    }

    [SkippableFact]
    public void OneSecondFromTheDefaultMicrophone_Is16kHzAudioOfAboutOneSecond()
    {
        Skip.If(_microphones.List().Count == 0, "No recording device on this machine.");
        var buffer = new CaptureBuffer();

        using (var capture = _microphones.Start(null, buffer))
        {
            Thread.Sleep(1000);
            Assert.True(capture.Stop());
        }

        // Stream start-up eats a few tens of ms; resampling to another rate would be off by a factor, not by ms.
        Assert.InRange(buffer.DurationMs, 850, 1150);
        Assert.False(buffer.Truncated);
    }

    [SkippableFact]
    public void UnknownDeviceId_IsNotFound()
    {
        Skip.If(_microphones.List().Count == 0, "No recording device on this machine.");

        var error = Assert.Throws<MicrophoneException>(() => _microphones.Start("{0.0.1.00000000}.{no-such-device}", new CaptureBuffer()));

        Assert.Equal(VoiceErrorCode.MicrophoneNotFound, error.Code);
    }
}
