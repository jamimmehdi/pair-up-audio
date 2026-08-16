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
    private readonly VolumeSampleProvider _volumeProvider;
    private readonly WasapiOut _output;
    private readonly WaveFormat _sourceFormat;
    private double _appliedDelayMs;

    public string DeviceId { get; }

    public OutputChannel(MMDevice device, WaveFormat sourceFormat)
    {
        DeviceId = device.ID;
        _sourceFormat = sourceFormat;

        _buffer = new BufferedWaveProvider(sourceFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(500)
        };

        ISampleProvider sampleChain = _buffer.ToSampleProvider();

        // Match channel count before resampling so WdlResampler only has to handle sample rate.
        if (sourceFormat.Channels == 1 && device.AudioClient.MixFormat.Channels == 2)
            sampleChain = new MonoToStereoSampleProvider(sampleChain);
        else if (sourceFormat.Channels == 2 && device.AudioClient.MixFormat.Channels == 1)
            sampleChain = new StereoToMonoSampleProvider(sampleChain);

        if (sourceFormat.SampleRate != device.AudioClient.MixFormat.SampleRate)
            sampleChain = new WdlResamplingSampleProvider(sampleChain, device.AudioClient.MixFormat.SampleRate);

        _volumeProvider = new VolumeSampleProvider(sampleChain) { Volume = 0.75f };

        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        _output.Init(_volumeProvider);
        _output.Play();
    }

    public void Feed(byte[] data, int bytesRecorded) => _buffer.AddSamples(data, 0, bytesRecorded);

    public float Volume
    {
        get => _volumeProvider.Volume;
        set => _volumeProvider.Volume = Math.Clamp(value, 0f, 1f);
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
        var deltaBytes = (int)(Math.Abs(deltaMs) * bytesPerMs);
        deltaBytes -= deltaBytes % _sourceFormat.BlockAlign;
        if (deltaBytes <= 0) return;

        var scratch = new byte[deltaBytes];
        if (deltaMs > 0)
            _buffer.AddSamples(scratch, 0, deltaBytes);
        else
            _buffer.Read(scratch, 0, deltaBytes);
    }

    public void Dispose()
    {
        _output.Stop();
        _output.Dispose();
    }
}
