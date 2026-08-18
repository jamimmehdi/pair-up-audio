using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PairUp.App.Audio;

/// <summary>
/// Renders captured audio to a single output device, independent of every other
/// OutputChannel, with its own volume and a resampler to match the device's mix format.
/// </summary>
public sealed class OutputChannel : IDisposable
{
    private readonly BufferedWaveProvider _buffer;
    private readonly DeviceProcessingSampleProvider _processingProvider;
    private readonly VolumeSampleProvider _volumeProvider;
    private readonly WasapiOut _output;
    private readonly WaveFormat _sourceFormat;
    private double _appliedDelayMs;
    private int _pendingTrimBytes;

    public string DeviceId { get; }

    public OutputChannel(MMDevice device, WaveFormat sourceFormat)
    {
        DeviceId = device.ID;
        _sourceFormat = sourceFormat;

        _buffer = new BufferedWaveProvider(sourceFormat)
        {
            DiscardOnBufferOverflow = true,
            // Needs enough headroom for the largest user-set delay (see the 1000ms slider max
            // in MainWindow.xaml) plus normal in-flight audio, or SetDelay's silence-padding
            // would silently get discarded instead of actually delaying playback.
            BufferDuration = TimeSpan.FromMilliseconds(1500)
        };

        ISampleProvider sampleChain = _buffer.ToSampleProvider();

        // Match channel count before resampling so WdlResampler only has to handle sample rate.
        if (sourceFormat.Channels == 1 && device.AudioClient.MixFormat.Channels == 2)
            sampleChain = new MonoToStereoSampleProvider(sampleChain);
        else if (sourceFormat.Channels == 2 && device.AudioClient.MixFormat.Channels == 1)
            sampleChain = new StereoToMonoSampleProvider(sampleChain);

        if (sourceFormat.SampleRate != device.AudioClient.MixFormat.SampleRate)
            sampleChain = new WdlResamplingSampleProvider(sampleChain, device.AudioClient.MixFormat.SampleRate);

        _processingProvider = new DeviceProcessingSampleProvider(sampleChain);
        _volumeProvider = new VolumeSampleProvider(_processingProvider) { Volume = 0.75f };

        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, ChooseLatencyMs(device));
        _output.Init(_volumeProvider);
        _output.Play();
    }

    /// <summary>
    /// Sizes the WASAPI render buffer from what the device's own driver reports it needs, rather
    /// than guessing from the device's name.
    ///
    /// This replaced a fixed 100ms buffer for everything, and then a name-based
    /// "Bluetooth gets 100ms, everything else gets 10ms" split. The name heuristic turned out
    /// to be unreliable in exactly the case it mattered: AirPods enumerate as
    /// "Headphones (AirPods Pro - ...)" with no "bluetooth" anywhere in the string, so a genuinely
    /// wireless device was already being treated as wired — while the 100ms branch only ever
    /// caught the Hands-Free telephony endpoints. Asking the driver for its own period sidesteps
    /// the guessing: a device that needs more headroom reports a longer period and gets it.
    ///
    /// Clamped to a 10ms floor because requesting below the shared-mode period just gets clamped
    /// by the audio engine anyway (measured: these endpoints report a 10ms period, with 3ms
    /// available only in exclusive mode), and to a 100ms ceiling so a pathological driver can't
    /// reintroduce the original latency problem.
    /// </summary>
    private static int ChooseLatencyMs(MMDevice device)
    {
        try
        {
            // DefaultDevicePeriod is in 100-nanosecond units.
            var periodMs = device.AudioClient.DefaultDevicePeriod / 10000.0;
            return (int)Math.Clamp(Math.Ceiling(periodMs), 10, 100);
        }
        catch
        {
            return 20; // Driver wouldn't say; pick a safe middle ground.
        }
    }

    /// <summary>
    /// Queues newly captured audio for this device. If the auto-sync balancer has scheduled a
    /// trim (see <see cref="TrimBacklog"/>), some of the newest audio is quietly dropped here
    /// instead — this is the only safe place to shrink this channel's queued backlog, since
    /// WasapiOut's own playback thread is concurrently calling Read() on the same buffer from
    /// the consumer side; dropping on the way in avoids racing that read.
    /// </summary>
    public void Feed(byte[] data, int bytesRecorded)
    {
        if (_pendingTrimBytes > 0)
        {
            var trimmed = Math.Min(_pendingTrimBytes, bytesRecorded);
            _pendingTrimBytes -= trimmed;
            if (trimmed >= bytesRecorded) return;
            _buffer.AddSamples(data, trimmed, bytesRecorded - trimmed);
            return;
        }

        _buffer.AddSamples(data, 0, bytesRecorded);
    }

    public float Volume
    {
        get => _volumeProvider.Volume;
        set => _volumeProvider.Volume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Low-shelf gain in dB, roughly -12..+12, for small speakers that need more bass.</summary>
    public double BassBoostDb
    {
        get => _processingProvider.BassGainDb;
        set => _processingProvider.BassGainDb = value;
    }

    /// <summary>High-shelf gain in dB, roughly -12..+12, to tame harsh/tinny earbuds.</summary>
    public double TrebleDb
    {
        get => _processingProvider.TrebleGainDb;
        set => _processingProvider.TrebleGainDb = value;
    }

    /// <summary>Sums stereo down to identical L/R so single-earbud devices don't lose panned content.</summary>
    public bool IsMono
    {
        get => _processingProvider.MonoDownmix;
        set => _processingProvider.MonoDownmix = value;
    }

    /// <summary>
    /// Offsets this channel's timeline relative to the others by prefilling (or draining)
    /// buffered audio, so faster devices can be held back to match a laggier one (e.g. Bluetooth).
    /// </summary>
    public void SetDelay(double targetMs)
    {
        targetMs = Math.Max(0, targetMs);
        var deltaMs = targetMs - _appliedDelayMs;
        var bytesPerMs = _sourceFormat.AverageBytesPerSecond / 1000.0;
        var deltaBytes = (int)(Math.Abs(deltaMs) * bytesPerMs);
        deltaBytes -= deltaBytes % _sourceFormat.BlockAlign;

        if (deltaBytes > 0)
        {
            var scratch = new byte[deltaBytes];
            if (deltaMs > 0)
                _buffer.AddSamples(scratch, 0, deltaBytes); // silence, pushes playback later
            else
                _buffer.Read(scratch, 0, deltaBytes); // drop buffered audio, pulls playback earlier
        }

        _appliedDelayMs = targetMs;
    }

    public TimeSpan BufferedDuration => _buffer.BufferedDuration;

    /// <summary>
    /// Small continuous correction used by the auto-sync balancer to counteract clock drift,
    /// independent of the user-set delay so the slider position stays stable.
    /// </summary>
    public void Nudge(double deltaMs)
    {
        var bytesPerMs = _sourceFormat.AverageBytesPerSecond / 1000.0;
        var deltaBytes = (int)(deltaMs * bytesPerMs);
        deltaBytes -= deltaBytes % _sourceFormat.BlockAlign;
        if (deltaBytes <= 0) return;

        var scratch = new byte[deltaBytes];
        _buffer.AddSamples(scratch, 0, deltaBytes);
    }

    /// <summary>
    /// Shrinks this channel's queued backlog by dropping upcoming captured audio at the feed
    /// side rather than reading it back out of the buffer — used by the auto-sync balancer to
    /// pull a device that's fallen behind (built up more queued audio than the others) back
    /// toward the pack, without racing WasapiOut's own concurrent reads from the same buffer.
    /// </summary>
    public void TrimBacklog(double deltaMs)
    {
        var bytesPerMs = _sourceFormat.AverageBytesPerSecond / 1000.0;
        var deltaBytes = (int)(deltaMs * bytesPerMs);
        deltaBytes -= deltaBytes % _sourceFormat.BlockAlign;
        if (deltaBytes <= 0) return;

        _pendingTrimBytes += deltaBytes;
    }

    public void Dispose()
    {
        _output.Stop();
        _output.Dispose();
    }
}
