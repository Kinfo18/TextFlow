using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TextFlow.Contracts.Voice;

namespace TextFlow.VoiceWorker.Audio;

/// <summary>
/// WASAPI shared-mode capture through NAudio (V0.2 H1.2). Asks Windows for 16 kHz mono PCM16 directly: its own
/// converter (AUTOCONVERTPCM) resamples and downmixes, so no resampler of ours runs. The default device is resolved on
/// every start, so a new default (headset plugged in) is used from the next dictation.
/// </summary>
internal sealed class WasapiMicrophones : IMicrophones
{
    private const int ENotFound = unchecked((int)0x80070490);
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);

    public IReadOnlyList<MicrophoneInfo> List()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var defaultId = DefaultDevice(devices) is { } device ? IdAndDispose(device) : null;
            var list = new List<MicrophoneInfo>();
            foreach (var endpoint in devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (endpoint)
                {
                    list.Add(new MicrophoneInfo(endpoint.ID, endpoint.FriendlyName, endpoint.ID == defaultId));
                }
            }

            return list;
        }
        catch (Exception ex) when (ex is not MicrophoneException)
        {
            throw Wrap(ex);
        }
    }

    public IMicrophoneCapture Start(string? microphoneId, CaptureBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        MMDevice? device = null;
        try
        {
            using var devices = new MMDeviceEnumerator();
            device = microphoneId is null
                ? DefaultDevice(devices) ?? throw new MicrophoneException(VoiceErrorCode.NoMicrophone)
                : devices.GetDevice(microphoneId);
            if (device.State != DeviceState.Active)
            {
                throw new MicrophoneException(VoiceErrorCode.MicrophoneNotFound);
            }

            return new Capture(device, buffer);
        }
        catch (Exception ex)
        {
            // NAudio may also throw ArgumentException, InvalidOperationException...: none may crash the worker.
            device?.Dispose();
            throw ex as MicrophoneException ?? Wrap(ex);
        }
    }

    private static MicrophoneException Wrap(Exception ex) => new(
        ex switch
        {
            UnauthorizedAccessException => VoiceErrorCode.MicrophoneAccessDenied,
            COMException com => Map(com.HResult),
            _ => VoiceErrorCode.Unknown,
        },
        ex);

    private static MMDevice? DefaultDevice(MMDeviceEnumerator devices) =>
        devices.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : null;

    private static string IdAndDispose(MMDevice device)
    {
        using (device)
        {
            return device.ID;
        }
    }

    private static VoiceErrorCode Map(int hresult) => hresult switch
    {
        ENotFound => VoiceErrorCode.MicrophoneNotFound,
        EAccessDenied => VoiceErrorCode.MicrophoneAccessDenied,
        AudclntEDeviceInvalidated => VoiceErrorCode.MicrophoneLost,
        _ => VoiceErrorCode.Unknown,
    };

    private sealed class Capture : IMicrophoneCapture
    {
        /// <summary>WASAPI delivers its last packet and raises RecordingStopped within one buffer (100 ms).</summary>
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(1);

        private readonly MMDevice _device;
        private readonly WasapiCapture _capture;
        private readonly TaskCompletionSource<Exception?> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _closed;

        public Capture(MMDevice device, CaptureBuffer buffer)
        {
            _device = device;
            Name = device.FriendlyName;
            _capture = new WasapiCapture(device, useEventSync: true)
            {
                WaveFormat = new WaveFormat(VoiceProtocol.SampleRate, 16, 1),
            };
            _capture.DataAvailable += (_, e) =>
            {
                if (!_closed) // a late packet after Dispose must not land in the next dictation's buffer
                {
                    buffer.WritePcm16(e.Buffer.AsSpan(0, e.BytesRecorded));
                }
            };
            _capture.RecordingStopped += (_, e) => _stopped.TrySetResult(e.Exception);
            try
            {
                _capture.StartRecording();
            }
            catch
            {
                _capture.Dispose();
                throw;
            }
        }

        public string Name { get; }

        public bool Stop()
        {
            // Stopped on its own before we asked: the device went away (with or without an error).
            var lostEarlier = _stopped.Task.IsCompleted;
            _capture.StopRecording();
            return _stopped.Task.Wait(StopTimeout) && !lostEarlier && _stopped.Task.Result is null;
        }

        public void Dispose()
        {
            _closed = true;
            _capture.Dispose();
            _device.Dispose();
        }
    }
}
