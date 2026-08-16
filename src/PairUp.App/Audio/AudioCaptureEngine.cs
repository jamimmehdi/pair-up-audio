using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PairUp.App.Audio;

/// <summary>
/// Captures whatever is playing on the current default output device via WASAPI loopback,
/// so it can be fanned out to other devices without disturbing the original playback.
/// </summary>
public sealed class AudioCaptureEngine : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _sourceDevice;

    public event Action<byte[], int>? DataAvailable;
    public WaveFormat? CaptureFormat => _capture?.WaveFormat;
    public string? SourceDeviceId { get; private set; }
    public bool IsCapturing { get; private set; }

    /// <summary>
    /// The captured device's OS volume control (the same one Windows' Quick Settings / volume
    /// flyout shows). Loopback capture doesn't naturally reflect changes to it, so the caller
    /// is expected to read/apply it as software gain manually.
    /// </summary>
    public AudioEndpointVolume? SourceEndpointVolume => _sourceDevice?.AudioEndpointVolume;

    public void Start()
    {
        if (IsCapturing) return;

        using var enumerator = new MMDeviceEnumerator();
        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        SourceDeviceId = defaultDevice.ID;
        _sourceDevice = defaultDevice;

        _capture = new WasapiLoopbackCapture(defaultDevice);
        _capture.DataAvailable += (_, e) => DataAvailable?.Invoke(e.Buffer, e.BytesRecorded);
        _capture.RecordingStopped += (_, _) => IsCapturing = false;
        _capture.StartRecording();
        IsCapturing = true;
    }

    public void Stop()
    {
        _capture?.StopRecording();
        IsCapturing = false;
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _capture = null;
        _sourceDevice?.Dispose();
        _sourceDevice = null;
    }
}
