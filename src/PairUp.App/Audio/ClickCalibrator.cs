using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PairUp.App.Audio;

/// <summary>
/// Plays a short repeating click through a single device on a known schedule, and measures how
/// long after each expected click the user taps a button — estimating that device's real
/// perceived audio delay (Bluetooth codec latency, amp lag, etc.) by ear, without needing a
/// microphone loopback measurement. Not a scientific instrument, just a better starting point
/// than guessing on a slider.
/// </summary>
public sealed class ClickCalibrator : IDisposable
{
    private const int PeriodMs = 1500;
    private const int PulseDurationMs = 70;
    private const double AssumedReactionTimeMs = 150;

    private WasapiOut? _output;
    private Stopwatch? _stopwatch;
    private readonly List<double> _tapDeltasMs = new();

    public bool IsRunning { get; private set; }
    public int TapCount => _tapDeltasMs.Count;

    public void Start(MMDevice device)
    {
        Stop();

        var mixFormat = device.AudioClient.MixFormat;
        var pulse = new PulseSampleProvider(mixFormat.SampleRate, mixFormat.Channels, PeriodMs, PulseDurationMs);

        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
        _output.Init(pulse);
        _tapDeltasMs.Clear();
        _stopwatch = Stopwatch.StartNew();
        _output.Play();
        IsRunning = true;
    }

    /// <summary>Call when the user taps upon hearing a click. Returns the raw delta recorded (ms).</summary>
    public double? RecordTap()
    {
        if (_stopwatch is null) return null;

        var elapsed = _stopwatch.Elapsed.TotalMilliseconds;
        var nearestClickTime = Math.Round(elapsed / PeriodMs) * PeriodMs;
        var delta = elapsed - nearestClickTime;

        // Ignore taps that aren't plausibly responding to an actual click (e.g. tapped
        // immediately, before the first click could have played).
        if (Math.Abs(delta) >= PeriodMs / 2.0) return null;

        _tapDeltasMs.Add(delta);
        return delta;
    }

    /// <summary>Median of recorded taps minus an assumed reaction time, clamped to the delay slider's range.</summary>
    public double? GetEstimatedDelayMs()
    {
        if (_tapDeltasMs.Count == 0) return null;

        var sorted = _tapDeltasMs.OrderBy(d => d).ToList();
        var median = sorted[sorted.Count / 2];
        return Math.Clamp(median - AssumedReactionTimeMs, 0, 1000);
    }

    public void Stop()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _stopwatch = null;
        IsRunning = false;
    }

    public void Dispose() => Stop();
}

/// <summary>Outputs a short 1kHz sine pulse at the start of every period, silence otherwise.</summary>
internal sealed class PulseSampleProvider : ISampleProvider
{
    private readonly int _periodSamples;
    private readonly int _pulseSamples;
    private readonly SignalGenerator _tone;
    private long _samplePosition;

    public WaveFormat WaveFormat { get; }

    public PulseSampleProvider(int sampleRate, int channels, int periodMs, int pulseMs)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _periodSamples = periodMs * sampleRate / 1000;
        _pulseSamples = pulseMs * sampleRate / 1000;
        _tone = new SignalGenerator(sampleRate, channels)
        {
            Type = SignalGeneratorType.Sin,
            Frequency = 1000,
            Gain = 0.5
        };
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var channels = WaveFormat.Channels;
        var frames = count / channels;
        var toneBuffer = new float[count];
        _tone.Read(toneBuffer, 0, count);

        for (var f = 0; f < frames; f++)
        {
            var posInPeriod = _samplePosition % _periodSamples;
            var inPulse = posInPeriod < _pulseSamples;

            for (var c = 0; c < channels; c++)
            {
                var idx = f * channels + c;
                buffer[offset + idx] = inPulse ? toneBuffer[idx] : 0f;
            }

            _samplePosition++;
        }

        return count;
    }
}
