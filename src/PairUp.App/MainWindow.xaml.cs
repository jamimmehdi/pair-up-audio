using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using PairUp.App.Audio;
using PairUp.App.Controls;
using PairUp.App.Services;

namespace PairUp.App;

public partial class MainWindow : Window
{
    public const string AppVersion = "0.2.0";

    private readonly AudioDeviceManager _deviceManager = new();
    private readonly MainWindowViewModel _viewModel = new();
    private readonly AudioEngine _audioEngine = new();
    private readonly SettingsStore _settingsStore = new();
    private bool _isRestoringSettings;
    private LayeredWaveVisualizer? _visualizer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        ThemeService.ApplyCurrentSystemTheme();
        LoadDevices();
        SourceInitialized += (_, _) =>
        {
            ApplyRoundedCorners();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        };
        Closed += (_, _) => { _visualizer?.Stop(); _audioEngine.Dispose(); };
        Loaded += (_, _) =>
        {
            // Start capture immediately so the visualizer has real audio to react to right away,
            // instead of only once the user connects a device or touches mute.
            _audioEngine.EnsureCaptureStarted();
            _visualizer = new LayeredWaveVisualizer(VisualizerCanvas, _audioEngine);
            _visualizer.Start();
        };
        _audioEngine.SyncStatusUpdated += AudioEngine_SyncStatusUpdated;
        _audioEngine.OsMasterVolumeChanged += OsMasterVolumeChanged;
        _audioEngine.OsMuteChanged += OsMuteChanged;
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void AudioEngine_SyncStatusUpdated(IReadOnlyList<SyncStatus> statuses)
    {
        Dispatcher.Invoke(() =>
        {
            foreach (var status in statuses)
            {
                var device = _viewModel.Devices.FirstOrDefault(d => d.Id == status.DeviceId);
                if (device is null) continue;

                device.SyncStatusText = status.IsReference
                    ? "REFERENCE"
                    : $"{(status.DriftMs >= 0 ? "+" : "")}{status.DriftMs:0}MS DRIFT";
            }
        });
    }

    private void OsMasterVolumeChanged(double percent)
    {
        Dispatcher.Invoke(() =>
        {
            _isRestoringSettings = true; // reflect only — the engine already applied this value
            try { _viewModel.MasterVolume = percent; }
            finally { _isRestoringSettings = false; }
        });
    }

    private void OsMuteChanged(bool muted)
    {
        Dispatcher.Invoke(() => _viewModel.IsSourceMuted = muted);
    }

    private void MuteSource_Click(object sender, RoutedEventArgs e)
    {
        var checkbox = (CheckBox)sender;
        if (!_audioEngine.TrySetSourceMuted(checkbox.IsChecked == true))
            checkbox.IsChecked = !checkbox.IsChecked; // revert — no source device available yet
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private void ApplyRoundedCorners()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    private const int WM_SETTINGCHANGE = 0x001A;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
            Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            ThemeService.ApplyCurrentSystemTheme();
        }

        return IntPtr.Zero;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var originalContent = button.Content;
        button.IsEnabled = false;
        button.Content = "CHECKING…";

        try
        {
            var result = await UpdateChecker.CheckAsync(AppVersion);

            if (result.UpdateAvailable)
            {
                if (result.InstallerDownloadUrl is null)
                {
                    // No installer asset published on the release — fall back to just opening it.
                    var openRelease = MessageBox.Show(
                        $"PairUp v{result.LatestVersion} is available — you're on v{AppVersion}.\n\n" +
                        "No installer was found attached to that release, so I can't install it " +
                        "automatically. Open the release page instead?",
                        "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (openRelease == MessageBoxResult.Yes)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = result.ReleaseUrl,
                            UseShellExecute = true
                        });
                    }
                    return;
                }

                var install = MessageBox.Show(
                    $"PairUp v{result.LatestVersion} is available — you're on v{AppVersion}.\n\n" +
                    "Download and install it now? PairUp will close during the install.",
                    "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (install != MessageBoxResult.Yes) return;

                var progress = new Progress<double>(p => button.Content = $"DOWNLOADING {p:0}%");
                var installerPath = await UpdateChecker.DownloadInstallerAsync(
                    result.InstallerDownloadUrl, result.InstallerAssetName!, AppVersion, progress);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                });

                Application.Current.Shutdown();
            }
            else
            {
                MessageBox.Show($"You're on the latest version (v{AppVersion}).", "PairUp",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't check for updates: {ex.Message}", "PairUp",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = true;
        }
    }

    private void CreditButton_Click(object sender, RoutedEventArgs e) => CreditPopup.IsOpen = !CreditPopup.IsOpen;

    private void CreditLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string url) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });

        CreditPopup.IsOpen = false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void LoadDevices()
    {
        var defaultDeviceId = _deviceManager.GetDefaultDeviceId();
        var (savedMasterVolume, savedSettings) = _settingsStore.Load();

        _isRestoringSettings = true;
        try
        {
            _viewModel.MasterVolume = savedMasterVolume;
            _audioEngine.SetMasterVolume(savedMasterVolume);
            _viewModel.Devices.Clear();
            foreach (var device in _deviceManager.GetOutputDevices())
            {
                device.IsSystemDefault = device.Id == defaultDeviceId;
                device.PropertyChanged += Device_PropertyChanged;

                if (savedSettings.TryGetValue(device.Id, out var saved))
                {
                    device.Volume = saved.Volume;
                    device.LatencyMs = saved.LatencyMs;
                    device.IsFavorite = saved.IsFavorite;
                    device.IsConnected = saved.IsConnected;
                }

                _viewModel.Devices.Add(device);
            }
        }
        finally
        {
            _isRestoringSettings = false;
        }

        var defaultDevice = _viewModel.Devices.FirstOrDefault(d => d.IsSystemDefault);
        _viewModel.SourceName = defaultDevice?.Name ?? "System Playback";
        _viewModel.DefaultDevice = defaultDevice;

        UpdateConnectedSummary();
        ResortDevices();
        RebuildFavorites();
        _viewModel.StatusText = $"PairUp v{AppVersion} · {_viewModel.Devices.Count} output device(s) detected";
    }

    private void SaveSettings()
    {
        var settings = _viewModel.Devices.Select(d =>
            new DeviceSettings(d.Id, d.IsConnected, d.Volume, d.LatencyMs, d.IsFavorite));
        _settingsStore.Save(_viewModel.MasterVolume, settings);
    }

    private void MasterVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // When _isRestoringSettings is set by OsMasterVolumeChanged, the engine already applied
        // this value (it's the source of the change) — re-calling SetMasterVolume here would
        // write the same value back to the OS endpoint and risk a notification ping-pong.
        if (_isRestoringSettings) return;

        _audioEngine.SetMasterVolume(e.NewValue);
        SaveSettings();
    }

    /// <summary>Connected devices float to the top of the main list; ties break alphabetically.</summary>
    private void ResortDevices()
    {
        var sorted = _viewModel.Devices.OrderByDescending(d => d.IsConnected).ThenBy(d => d.Name).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var currentIndex = _viewModel.Devices.IndexOf(sorted[i]);
            if (currentIndex != i)
                _viewModel.Devices.Move(currentIndex, i);
        }
    }

    private void RebuildFavorites()
    {
        _viewModel.FavoriteDevices.Clear();
        foreach (var device in _viewModel.Devices.Where(d => d.IsFavorite).OrderBy(d => d.Name))
            _viewModel.FavoriteDevices.Add(device);

        _viewModel.HasFavorites = _viewModel.FavoriteDevices.Count > 0;
    }

    private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AudioDeviceInfo device) return;

        switch (e.PropertyName)
        {
            case nameof(AudioDeviceInfo.IsConnected):
                if (device.IsConnected)
                {
                    var result = _audioEngine.TryAddOutput(device.Id, device.Volume, device.LatencyMs);

                    if (result == AddOutputResult.WouldEcho)
                    {
                        device.IsConnected = false; // triggers the else-branch below, clearing IsOutOfRange
                        MessageBox.Show(
                            $"Can't add \"{device.Name}\" — it's currently your system's default " +
                            "playback device, so mirroring to it would cause an echo. Switch your " +
                            "default output first, or pick a different device.",
                            "PairUp", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else if (result == AddOutputResult.DeviceUnavailable)
                    {
                        // Set IsConnected=false first (re-enters this handler and clears IsOutOfRange
                        // via the else-branch), then set IsOutOfRange=true after so it isn't wiped out.
                        device.IsConnected = false;
                        device.IsOutOfRange = true;
                        MessageBox.Show(
                            $"Can't reach \"{device.Name}\" right now — it may be out of Bluetooth " +
                            "range or turned off. It'll show as Out of Range until you reconnect it.",
                            "PairUp", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    _audioEngine.RemoveOutput(device.Id);
                    device.IsOutOfRange = false;
                }
                UpdateConnectedSummary();
                ResortDevices();
                break;

            case nameof(AudioDeviceInfo.Volume):
                _audioEngine.SetVolume(device.Id, device.Volume);
                break;

            case nameof(AudioDeviceInfo.LatencyMs):
                _audioEngine.SetDelay(device.Id, device.LatencyMs);
                break;

            case nameof(AudioDeviceInfo.IsFavorite):
                RebuildFavorites();
                break;
        }

        if (!_isRestoringSettings)
            SaveSettings();
    }

    private void UpdateConnectedSummary()
    {
        var connected = _viewModel.Devices.Count(d => d.IsConnected);
        _viewModel.ConnectedSummary = $"{connected} of {_viewModel.Devices.Count} devices playing in sync";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var originalContent = button.Content;
        button.IsEnabled = false;
        button.Content = "REFRESHING…";

        // LoadDevices runs synchronously on the UI thread (WASAPI enumeration needs the STA
        // thread), so without yielding here the "Refreshing…" label would never actually paint —
        // the whole operation would complete within a single frame.
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);

        LoadDevices();

        button.Content = originalContent;
        button.IsEnabled = true;
    }

    private void FavoriteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is AudioDeviceInfo device)
            device.IsFavorite = !device.IsFavorite;
    }
}

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> FavoriteDevices { get; } = new();

    private bool _hasFavorites;
    public bool HasFavorites
    {
        get => _hasFavorites;
        set { _hasFavorites = value; OnPropertyChanged(); }
    }

    private string _statusText = "PairUp";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private string _connectedSummary = "0 of 0 devices playing in sync";
    public string ConnectedSummary
    {
        get => _connectedSummary;
        set { _connectedSummary = value; OnPropertyChanged(); }
    }

    private string _sourceName = "System Playback";
    public string SourceName
    {
        get => _sourceName;
        set { _sourceName = value; OnPropertyChanged(); }
    }

    private double _masterVolume = 100;
    public double MasterVolume
    {
        get => _masterVolume;
        set { _masterVolume = value; OnPropertyChanged(); }
    }

    private bool _isSourceMuted;
    public bool IsSourceMuted
    {
        get => _isSourceMuted;
        set { _isSourceMuted = value; OnPropertyChanged(); }
    }

    private AudioDeviceInfo? _defaultDevice;
    public AudioDeviceInfo? DefaultDevice
    {
        get => _defaultDevice;
        set { _defaultDevice = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDefaultDevice)); }
    }

    public bool HasDefaultDevice => DefaultDevice is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
