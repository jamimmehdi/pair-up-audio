using NAudio.Dsp;
using NAudio.Wave;

namespace PairUp.App.Audio;

/// <summary>
/// Per-device audio processing stage: bass/treble shelving EQ (useful for small speakers that
/// need a low-end lift, or tinny earbuds that need treble tamed) and mono downmix (for
/// single-earbud devices, so panned content doesn't lose half the mix to the missing ear).
/// Sits in the render chain before volume, so volume stays the final gain stage.
/// </summary>
public sealed class DeviceProcessingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private BiQuadFilter[] _bassFilters;
    private BiQuadFilter[] _trebleFilters;
    private double _bassGainDb;
    private double _trebleGainDb;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public bool MonoDownmix { get; set; }

    public double BassGainDb
    {
        get => _bassGainDb;
        set { _bassGainDb = value; RebuildFilters(); }
    }

    public double TrebleGainDb
    {
        get => _trebleGainDb;
        set { _trebleGainDb = value; RebuildFilters(); }
    }

    public DeviceProcessingSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _bassFilters = new BiQuadFilter[_channels];
        _trebleFilters = new BiQuadFilter[_channels];
        RebuildFilters();
    }

    private void RebuildFilters()
    {
        var sampleRate = _source.WaveFormat.SampleRate;
        for (var c = 0; c < _channels; c++)
        {
            // 150Hz low shelf for bass, 4kHz high shelf for treble — broad, musical ranges
            // rather than a surgical parametric EQ, since this is meant for "small speaker
            // needs more bass" / "earbuds are harsh", not studio mixing.
            _bassFilters[c] = BiQuadFilter.LowShelf(sampleRate, 150, 0.707f, (float)_bassGainDb);
            _trebleFilters[c] = BiQuadFilter.HighShelf(sampleRate, 4000, 0.707f, (float)_trebleGainDb);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var samplesRead = _source.Read(buffer, offset, count);

        if (MonoDownmix && _channels == 2)
        {
            for (var i = 0; i + 1 < samplesRead; i += 2)
            {
                var mixed = (buffer[offset + i] + buffer[offset + i + 1]) * 0.5f;
                buffer[offset + i] = mixed;
                buffer[offset + i + 1] = mixed;
            }
        }

        if (_bassGainDb != 0 || _trebleGainDb != 0)
        {
            for (var i = 0; i < samplesRead; i++)
            {
                var channel = i % _channels;
                var sample = buffer[offset + i];
                sample = _bassFilters[channel].Transform(sample);
                sample = _trebleFilters[channel].Transform(sample);
                buffer[offset + i] = sample;
            }
        }

        return samplesRead;
    }
}
