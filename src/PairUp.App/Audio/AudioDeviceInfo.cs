using System.ComponentModel;
using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;

namespace PairUp.App.Audio;

public enum DeviceKind
{
    Bluetooth,
    Wired,
    Speakers,
    Other
}

public sealed class AudioDeviceInfo : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DeviceKind Kind { get; init; }
    public required DeviceState State { get; init; }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); }
    }

    private double _volume = 75;
    public double Volume
    {
        get => _volume;
        set { _volume = value; OnPropertyChanged(); }
    }

    private double _latencyMs;
    public double LatencyMs
    {
        get => _latencyMs;
        set { _latencyMs = value; OnPropertyChanged(); }
    }

    private bool _isSystemDefault;
    public bool IsSystemDefault
    {
        get => _isSystemDefault;
        set { _isSystemDefault = value; OnPropertyChanged(); }
    }

    private bool _isOutOfRange;
    public bool IsOutOfRange
    {
        get => _isOutOfRange;
        set { _isOutOfRange = value; OnPropertyChanged(); }
    }

    private string _syncStatusText = "SYNCED";
    public string SyncStatusText
    {
        get => _syncStatusText;
        set { _syncStatusText = value; OnPropertyChanged(); }
    }

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set { _isFavorite = value; OnPropertyChanged(); }
    }

    private double _bassBoost;
    /// <summary>Low-shelf gain in dB, -12..+12. Positive = more bass, useful for small speakers.</summary>
    public double BassBoost
    {
        get => _bassBoost;
        set { _bassBoost = value; OnPropertyChanged(); }
    }

    private double _treble;
    /// <summary>High-shelf gain in dB, -12..+12. Negative tames harsh/tinny earbuds.</summary>
    public double Treble
    {
        get => _treble;
        set { _treble = value; OnPropertyChanged(); }
    }

    private bool _isMono;
    /// <summary>Sums stereo to identical L/R — for single-earbud devices so panned audio isn't lost.</summary>
    public bool IsMono
    {
        get => _isMono;
        set { _isMono = value; OnPropertyChanged(); }
    }

    private bool _isProcessingExpanded;
    /// <summary>UI-only: whether the EQ/mono panel is expanded for this device row.</summary>
    public bool IsProcessingExpanded
    {
        get => _isProcessingExpanded;
        set { _isProcessingExpanded = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
