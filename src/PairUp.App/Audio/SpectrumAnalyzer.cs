using NAudio.Dsp;
using NAudio.Wave;

namespace PairUp.App.Audio;

/// <summary>
/// Rolling FFT spectrum analyzer fed from the live captured audio stream, so the UI can poll
/// current frequency-band levels each frame to drive a reactive visualizer.
/// </summary>
public sealed class SpectrumAnalyzer
{
    private const int FftLength = 2048;   // must be a power of two
    private const int FftPow2 = 11;       // log2(FftLength)

    private readonly float[] _ring = new float[FftLength];
    private int _writeIndex;
    private readonly object _lock = new();

    public void Feed(byte[] data, int bytesRecorded, WaveFormat format)
    {
        // WASAPI loopback almost always reports its format as WAVE_FORMAT_EXTENSIBLE (wrapping
        // IEEE float internally) rather than IeeeFloat directly — checking Encoding alone
        // rejected every buffer here. 32-bit is a safe float assumption for WASAPI's shared-mode
        // mixing engine, which is what everything else in this app already relies on implicitly.
        if (format.BitsPerSample != 32)
            return;

        var channels = format.Channels;
        var frameCount = bytesRecorded / (4 * channels);

        lock (_lock)
        {
            for (var frame = 0; frame < frameCount; frame++)
            {
                var sum = 0f;
                var baseOffset = frame * channels * 4;
                for (var ch = 0; ch < channels; ch++)
                    sum += BitConverter.ToSingle(data, baseOffset + ch * 4);

                _ring[_writeIndex] = sum / channels;
                _writeIndex = (_writeIndex + 1) % FftLength;
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="bandCount"/> normalized (roughly 0..1) magnitude levels spanning
    /// the audible spectrum on a logarithmic scale, computed from the most recently captured audio.
    /// </summary>
    public float[] GetBands(int bandCount)
    {
        var complex = new Complex[FftLength];

        lock (_lock)
        {
            for (var i = 0; i < FftLength; i++)
            {
                var sampleIndex = (_writeIndex + i) % FftLength;
                // Hann window to reduce spectral leakage.
                var window = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (FftLength - 1)));
                complex[i].X = _ring[sampleIndex] * window;
                complex[i].Y = 0;
            }
        }

        FastFourierTransform.FFT(true, FftPow2, complex);

        var bands = new float[bandCount];
        var usableBins = FftLength / 2;

        for (var b = 0; b < bandCount; b++)
        {
            // Logarithmic bin grouping so low frequencies (bass) get meaningful resolution
            // instead of being crushed into the first band.
            var loBin = (int)MathF.Pow(usableBins, b / (float)bandCount);
            var hiBin = (int)MathF.Pow(usableBins, (b + 1) / (float)bandCount);
            hiBin = Math.Max(hiBin, loBin + 1);
            hiBin = Math.Min(hiBin, usableBins);

            var magnitude = 0f;
            for (var bin = loBin; bin < hiBin; bin++)
            {
                var re = complex[bin].X;
                var im = complex[bin].Y;
                magnitude = Math.Max(magnitude, MathF.Sqrt(re * re + im * im));
            }

            // Rough dB-ish compression so quiet content still produces visible motion.
            var db = 20 * MathF.Log10(magnitude + 1e-6f);
            bands[b] = Math.Clamp((db + 60) / 60, 0f, 1f);
        }

        return bands;
    }
}
