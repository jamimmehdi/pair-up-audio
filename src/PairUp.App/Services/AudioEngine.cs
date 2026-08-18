using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using PairUp.App.Audio;

namespace PairUp.App.Services;

public enum AddOutputResult
{
    Success,
    WouldEcho,
    DeviceUnavailable
}

public sealed record SyncStatus(string DeviceId, double DriftMs, bool IsReference);

/// <summary>
/// Orchestrates one loopback capture from the current default device and fans it out
/// to any number of independently-controlled output devices.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly AudioCaptureEngine _capture = new();
    private readonly SpectrumAnalyzer _spectrum = new();
    private readonly Dictionary<string, OutputChannel> _channels = new();
    private readonly Dictionary<string, double> _requestedVolumePercent = new();
    private readonly object _lock = new();
    private Timer? _syncTimer;
    private double _masterVolumePercent = 100;

    private const double SyncIntervalMs = 400;
    private const double SyncDeadZoneMs = 6;   // ignore differences smaller than this to avoid constant micro-jitter
    private const double SyncMaxStepMs = 12;   // cap per-tick correction so nudges stay inaudible

    public event Action<IReadOnlyList<SyncStatus>>? SyncStatusUpdated;

    /// <summary>Raised when the OS volume control for the captured device changes externally
    /// (Windows' volume flyout, keyboard media keys, etc.), so the UI can mirror it.</summary>
    public event Action<double>? OsMasterVolumeChanged;

    /// <summary>Raised when the source device's mute state changes externally (Windows' volume
    /// flyout, keyboard mute key, etc.), so the UI can mirror it.</summary>
    public event Action<bool>? OsMuteChanged;

    private bool _osVolumeHooked;
    private bool _desiredMute;
    private bool _applyingMasterVolume;
    private Timer? _muteGuardTimer;
    private const int MuteGuardWindowMs = 400;

    public string? SourceDeviceId => _capture.SourceDeviceId;

    public void EnsureCaptureStarted()
    {
        if (!_capture.IsCapturing)
        {
            _capture.DataAvailable += OnDataAvailable;
            _capture.Start();
        }

        _syncTimer ??= new Timer(_ => RebalanceChannels(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(SyncIntervalMs));

        if (!_osVolumeHooked && _capture.SourceEndpointVolume is { } endpointVolume)
        {
            endpointVolume.OnVolumeNotification += OnOsVolumeNotification;
            _osVolumeHooked = true;
        }
    }

    private void OnOsVolumeNotification(AudioVolumeNotificationData data)
    {
        var percent = data.MasterVolume * 100;
        var volumeChanged = false;

        lock (_lock)
        {
            // Ignore notifications caused by our own SetMasterVolume writes.
            if (Math.Abs(percent - _masterVolumePercent) >= 0.5)
            {
                volumeChanged = true;
                _masterVolumePercent = percent;
                foreach (var (deviceId, channel) in _channels)
                {
                    var requested = _requestedVolumePercent.GetValueOrDefault(deviceId, 75);
                    channel.Volume = (float)(_masterVolumePercent / 100.0 * requested / 100.0);
                }
            }

            // While we're mid-write in SetMasterVolume, the OS's own auto-unmute-on-volume-change
            // fires a transient notification with Muted=false before we get to reassert the real
            // value — ignore notifications during that window rather than trusting them as genuine.
            if (!_applyingMasterVolume)
                _desiredMute = data.Muted;
        }

        if (volumeChanged)
            OsMasterVolumeChanged?.Invoke(percent);

        if (!_applyingMasterVolume)
            OsMuteChanged?.Invoke(_desiredMute);
    }

    /// <summary>
    /// Runs continuously while 2+ devices are connected: keeps every channel's buffered
    /// duration converged toward the group's fastest (least backlogged) channel, smoothly
    /// correcting the clock-drift that otherwise pulls independently-clocked output devices
    /// out of sync over time.
    ///
    /// Converges toward the minimum, not the maximum: a device with more inherent buffering
    /// (e.g. a Bluetooth speaker with a larger driver/codec queue) naturally sits at a higher
    /// BufferedDuration than the rest. Converging everyone else up to match it — the previous
    /// behavior — forced every other device to accumulate extra artificial delay just to equal
    /// the laziest device in the group, and since that correction only ever added silence and
    /// never removed it, the group's baseline latency could only ratchet upward over a session,
    /// getting worse the more devices (and thus the more chances for one outlier) were added.
    /// Converging down to the fastest device instead trims the backlogged outlier toward the
    /// pack via OutputChannel.TrimBacklog (safe: drops upcoming input at the feed side rather
    /// than reading back out of the buffer WasapiOut's thread is concurrently consuming from).
    /// </summary>
    private void RebalanceChannels()
    {
        List<SyncStatus>? statuses = null;

        lock (_lock)
        {
            if (_channels.Count < 2) return;

            var target = _channels.Values.Min(c => c.BufferedDuration.TotalMilliseconds);
            statuses = new List<SyncStatus>(_channels.Count);

            foreach (var (deviceId, channel) in _channels)
            {
                var diff = target - channel.BufferedDuration.TotalMilliseconds;
                if (diff <= -SyncDeadZoneMs)
                    channel.TrimBacklog(Math.Min(-diff, SyncMaxStepMs));
                else if (diff >= SyncDeadZoneMs)
                    channel.Nudge(Math.Min(diff, SyncMaxStepMs));

                var isReference = channel.BufferedDuration.TotalMilliseconds <= target + 0.5;
                statuses.Add(new SyncStatus(deviceId, -diff, isReference));
            }
        }

        SyncStatusUpdated?.Invoke(statuses);
    }

    /// <summary>
    /// WouldEcho: the requested device is the same one currently being captured from.
    /// DeviceUnavailable: the device couldn't be opened (e.g. a Bluetooth device that's
    /// paired but out of range, or was unplugged since the last device list refresh).
    /// </summary>
    public AddOutputResult TryAddOutput(
        string deviceId, double volumePercent = 75, double delayMs = 0,
        double bassBoostDb = 0, double trebleDb = 0, bool isMono = false)
    {
        EnsureCaptureStarted();

        if (deviceId == _capture.SourceDeviceId)
            return AddOutputResult.WouldEcho;

        lock (_lock)
        {
            if (_channels.ContainsKey(deviceId))
                return AddOutputResult.Success;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(deviceId);
                var channel = new OutputChannel(device, _capture.CaptureFormat!);
                channel.SetDelay(delayMs);
                channel.BassBoostDb = bassBoostDb;
                channel.TrebleDb = trebleDb;
                channel.IsMono = isMono;
                _requestedVolumePercent[deviceId] = volumePercent;
                channel.Volume = (float)(_masterVolumePercent / 100.0 * volumePercent / 100.0);
                _channels[deviceId] = channel;
            }
            catch (Exception)
            {
                return AddOutputResult.DeviceUnavailable;
            }
        }

        return AddOutputResult.Success;
    }

    public void RemoveOutput(string deviceId)
    {
        lock (_lock)
        {
            if (_channels.Remove(deviceId, out var channel))
                channel.Dispose();
            _requestedVolumePercent.Remove(deviceId);
        }
    }

    public void SetBassBoost(string deviceId, double gainDb)
    {
        lock (_lock)
        {
            if (_channels.TryGetValue(deviceId, out var channel))
                channel.BassBoostDb = gainDb;
        }
    }

    public void SetTreble(string deviceId, double gainDb)
    {
        lock (_lock)
        {
            if (_channels.TryGetValue(deviceId, out var channel))
                channel.TrebleDb = gainDb;
        }
    }

    public void SetMono(string deviceId, bool isMono)
    {
        lock (_lock)
        {
            if (_channels.TryGetValue(deviceId, out var channel))
                channel.IsMono = isMono;
        }
    }

    public void SetVolume(string deviceId, double volumePercent)
    {
        lock (_lock)
        {
            _requestedVolumePercent[deviceId] = volumePercent;
            if (_channels.TryGetValue(deviceId, out var channel))
                channel.Volume = (float)(_masterVolumePercent / 100.0 * volumePercent / 100.0);
        }
    }

    /// <summary>
    /// Applied as software gain against every channel (Windows' own volume flyout doesn't
    /// reliably reach a WASAPI loopback capture tap — it typically applies at the hardware/driver
    /// stage, after where the loopback is captured — so PairUp reads/writes the OS endpoint
    /// volume itself and enforces it manually, keeping both controls in sync and both working).
    /// </summary>
    public void SetMasterVolume(double volumePercent)
    {
        lock (_lock)
        {
            _masterVolumePercent = volumePercent;
            foreach (var (deviceId, channel) in _channels)
            {
                var requested = _requestedVolumePercent.GetValueOrDefault(deviceId, 75);
                channel.Volume = (float)(_masterVolumePercent / 100.0 * requested / 100.0);
            }

            if (_capture.SourceEndpointVolume is { } endpointVolume)
            {
                // Setting MasterVolumeLevelScalar auto-unmutes the endpoint (same as a physical
                // volume knob), and the resulting OnVolumeNotification callback is delivered
                // asynchronously on its own thread — it can arrive well after this method
                // returns, so a guard that resets immediately here doesn't actually suppress it.
                // Keep the guard open for a short grace period instead, and always reassert our
                // own tracked _desiredMute rather than trusting a live re-query.
                _applyingMasterVolume = true;
                _muteGuardTimer?.Dispose();
                _muteGuardTimer = new Timer(_ => _applyingMasterVolume = false, null, MuteGuardWindowMs, Timeout.Infinite);

                endpointVolume.MasterVolumeLevelScalar = (float)(volumePercent / 100.0);
                endpointVolume.Mute = _desiredMute;
            }
        }
    }

    public void SetDelay(string deviceId, double delayMs)
    {
        lock (_lock)
        {
            if (_channels.TryGetValue(deviceId, out var channel))
                channel.SetDelay(delayMs);
        }
    }

    /// <summary>
    /// Mutes the source device's own OS output directly (the device PairUp is capturing from),
    /// so it can be silenced while audio still fans out to the connected devices — there's no
    /// virtual driver making the source silent by default anymore.
    /// </summary>
    public bool TrySetSourceMuted(bool muted)
    {
        EnsureCaptureStarted();

        if (_capture.SourceEndpointVolume is not { } endpointVolume)
            return false;

        lock (_lock)
        {
            _desiredMute = muted;
            endpointVolume.Mute = muted;
        }

        return true;
    }

    public bool IsSourceMuted => _desiredMute;

    private void OnDataAvailable(byte[] buffer, int bytesRecorded)
    {
        lock (_lock)
        {
            foreach (var channel in _channels.Values)
                channel.Feed(buffer, bytesRecorded);
        }

        if (_capture.CaptureFormat is { } format)
            _spectrum.Feed(buffer, bytesRecorded, format);
    }

    /// <summary>Polled by the UI (e.g. once per rendered frame) to drive the audio visualizer.</summary>
    public float[] GetSpectrum(int bandCount) => _spectrum.GetBands(bandCount);

    public void Dispose()
    {
        _syncTimer?.Dispose();
        _muteGuardTimer?.Dispose();

        if (_osVolumeHooked && _capture.SourceEndpointVolume is { } endpointVolume)
            endpointVolume.OnVolumeNotification -= OnOsVolumeNotification;

        lock (_lock)
        {
            foreach (var channel in _channels.Values)
                channel.Dispose();
            _channels.Clear();
        }

        _capture.Dispose();
    }
}
