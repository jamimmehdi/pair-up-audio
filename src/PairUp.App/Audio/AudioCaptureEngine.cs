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
        ReduceCaptureBufferLatency(_capture);
        _capture.DataAvailable += (_, e) => DataAvailable?.Invoke(e.Buffer, e.BytesRecorded);
        _capture.RecordingStopped += (_, _) => IsCapturing = false;
        _capture.StartRecording();
        IsCapturing = true;
    }

    /// <summary>
    /// NAudio sizes WasapiCapture's internal buffer at 100ms and doesn't expose that publicly,
    /// which alone dominated PairUp's added latency: measured, the default delivered captured
    /// audio in ~62ms chunks, so nothing could reach an output device sooner than that. Dropping
    /// it to 20ms cuts delivery to ~16ms chunks (~47ms saved). Going lower gains nothing — the
    /// shared-mode device period floors it around 16ms regardless.
    /// Set via reflection since there's no public setter; failure is non-fatal (we just keep
    /// NAudio's default) so a future NAudio version renaming the field can't break capture.
    /// </summary>
    private static void ReduceCaptureBufferLatency(WasapiCapture capture)
    {
        try
        {
            typeof(WasapiCapture)
                .GetField("audioBufferMillisecondsLength",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(capture, CaptureBufferMs);
        }
        catch
        {
            // Keep NAudio's default buffer; higher latency, but capture still works.
        }
    }

    private const int CaptureBufferMs = 20;

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
