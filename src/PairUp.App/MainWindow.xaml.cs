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
    public const string AppVersion = "0.3.6";

    private readonly AudioDeviceManager _deviceManager = new();
    private readonly MainWindowViewModel _viewModel = new();
    private readonly AudioEngine _audioEngine = new();
    private readonly SettingsStore _settingsStore = new();
    private bool _isRestoringSettings;
    private LayeredWaveVisualizer? _visualizer;
    private GuestServer? _guestServer;
    private readonly ClickCalibrator _calibrator = new();
    private AudioDeviceInfo? _calibratingDevice;
    private const int CalibrationTapTarget = 5;
    private TrayIconService? _tray;
    private bool _hasShownTrayBalloon;

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
        Closed += (_, _) =>
        {
            _visualizer?.Stop();
            _audioEngine.Dispose();
            _guestServer?.Dispose();
            _calibrator.Dispose();
            _tray?.Dispose();

            // NAudio's native WASAPI capture/render threads aren't always true .NET background
            // threads, so the process can outlive every window by several seconds even after
            // full disposal — which the update installer sees as the exe still being locked and
            // fails to close it. Force a real process exit once cleanup is done.
            Environment.Exit(0);
        };

        _tray = new TrayIconService(
            () => _viewModel.Devices,
            device => device.IsConnected = !device.IsConnected,
            RestoreFromTray,
            ExitApp)
        { Visible = true };

        Loaded += (_, _) =>
        {
            // Start capture immediately so the visualizer has real audio to react to right away,
            // instead of only once the user connects a device or touches mute.
            _audioEngine.EnsureCaptureStarted();
            _visualizer = new LayeredWaveVisualizer(VisualizerCanvas, _audioEngine);
            _visualizer.Start();

            _guestServer = new GuestServer(() => _viewModel.Devices, Dispatcher);
            try { _guestServer.Start(); }
            catch { /* Guest sharing just won't be available; nothing else depends on it. */ }

            _ = CheckForUpdatesOnLaunchAsync();
        };
        _audioEngine.SyncStatusUpdated += AudioEngine_SyncStatusUpdated;
        _audioEngine.OsMasterVolumeChanged += OsMasterVolumeChanged;
        _audioEngine.OsMuteChanged += OsMuteChanged;
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                ShowInTaskbar = false;
                if (!_hasShownTrayBalloon)
                {
                    _hasShownTrayBalloon = true;
                    _tray?.ShowBalloon("PairUp", "Still running in the tray — double-click the icon to reopen, or right-click to toggle devices.");
                }
            }
        };
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitApp() => Dispatcher.Invoke(Close);

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
                    "Download and install it now? PairUp will close, and the installer will open.\n\n" +
                    "Since the installer isn't code-signed, Windows may show a \"Windows protected " +
                    "your PC\" prompt — click \"More info\" then \"Run anyway\" to continue.",
                    "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (install != MessageBoxResult.Yes) return;

                UpdateProgressBar.Value = 0;
                UpdateProgressBar.Visibility = Visibility.Visible;
                var progress = new Progress<double>(p =>
                {
                    button.Content = $"DOWNLOADING {p:0}%";
                    UpdateProgressBar.Value = p;
                });
                var installerPath = await UpdateChecker.DownloadInstallerAsync(
                    result.InstallerDownloadUrl, result.InstallerAssetName!, AppVersion, progress);

                // Not silent: SmartScreen's "Windows protected your PC" prompt still fires for an
                // unsigned installer regardless of /VERYSILENT, so running silently just hides the
                // one thing the user actually needs to click through — the install would silently
                // stall with nothing visible happening. Letting the installer show its own UI (and
                // that prompt, if it appears) means the user can actually see and complete it; our
                // [Run] entry relaunches PairUp automatically once the wizard finishes.
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
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            ResetUpdateButtonStyle();
            RefreshStatusText();
        }
    }

    /// <summary>
    /// Quietly checks for an update shortly after launch, with no "Checking…" UI and no popup
    /// on failure or when already up to date — only surfaces something when there's genuinely a
    /// newer version, via a status line, an accented Check for Updates button, and a tray balloon.
    /// </summary>
    private async Task CheckForUpdatesOnLaunchAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var result = await UpdateChecker.CheckAsync(AppVersion);
            if (!result.UpdateAvailable) return;

            _viewModel.StatusText = $"Update available — v{result.LatestVersion} (you're on v{AppVersion}). Click Check for Updates.";
            CheckUpdateButton.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
            CheckUpdateButton.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x15, 0x12));
            _tray?.ShowBalloon("PairUp update available",
                $"Version {result.LatestVersion} is ready — open PairUp and click Check for Updates to install.");
        }
        catch
        {
            // Silent by design — a background check shouldn't interrupt anyone with network errors.
        }
    }

    private void ResetUpdateButtonStyle()
    {
        CheckUpdateButton.Background = (System.Windows.Media.Brush)FindResource("Surface3Brush");
        CheckUpdateButton.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
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

    private void LicenseLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        CreditPopup.IsOpen = false;
        LicensePopup.IsOpen = true;
    }

    private void LicenseClose_Click(object sender, RoutedEventArgs e) => LicensePopup.IsOpen = false;

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // The window's own X button minimizes to tray rather than exiting (matching the tray-mode
    // feature); every other close path — Application.Shutdown() during self-update, the tray's
    // own Exit item, or an external close request like the update installer's Restart Manager
    // check — should actually close the app, so there's no blanket Closing handler to fight them.
    private void Close_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

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
                    device.BassBoost = saved.BassBoost;
                    device.Treble = saved.Treble;
                    device.IsMono = saved.IsMono;
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
        RefreshStatusText();
    }

    private void RefreshStatusText() =>
        _viewModel.StatusText = $"PairUp v{AppVersion} · {_viewModel.Devices.Count} output device(s) detected";

    private void SaveSettings()
    {
        var settings = _viewModel.Devices.Select(d =>
            new DeviceSettings(d.Id, d.IsConnected, d.Volume, d.LatencyMs, d.IsFavorite,
                d.BassBoost, d.Treble, d.IsMono));
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
                    var result = _audioEngine.TryAddOutput(
                        device.Id, device.Volume, device.LatencyMs,
                        device.BassBoost, device.Treble, device.IsMono);

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

            case nameof(AudioDeviceInfo.BassBoost):
                _audioEngine.SetBassBoost(device.Id, device.BassBoost);
                break;

            case nameof(AudioDeviceInfo.Treble):
                _audioEngine.SetTreble(device.Id, device.Treble);
                break;

            case nameof(AudioDeviceInfo.IsMono):
                _audioEngine.SetMono(device.Id, device.IsMono);
                break;

            case nameof(AudioDeviceInfo.IsProcessingExpanded):
                return; // UI-only, nothing to forward to the engine or save
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

    private void EqToggle_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is AudioDeviceInfo device)
            device.IsProcessingExpanded = !device.IsProcessingExpanded;
    }

    private void CalibrateToggle_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not AudioDeviceInfo device) return;

        _calibrator.Stop();
        _calibratingDevice = device;

        CalibratePopup.PlacementTarget = button;
        CalibrateDeviceName.Text = device.Name;
        CalibrateNotEnoughText.Visibility = Visibility.Collapsed;
        CalibrateIdlePanel.Visibility = Visibility.Visible;
        CalibrateRunningPanel.Visibility = Visibility.Collapsed;
        CalibrateDonePanel.Visibility = Visibility.Collapsed;

        CalibratePopup.IsOpen = true;
    }

    private void CalibrateStart_Click(object sender, RoutedEventArgs e)
    {
        if (_calibratingDevice is null) return;

        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var device = enumerator.GetDevice(_calibratingDevice.Id);
            _calibrator.Start(device);
        }
        catch
        {
            CalibratePopup.IsOpen = false;
            return;
        }

        CalibrateTapCountText.Text = $"0 of {CalibrationTapTarget} taps";
        CalibrateNotEnoughText.Visibility = Visibility.Collapsed;
        CalibrateIdlePanel.Visibility = Visibility.Collapsed;
        CalibrateDonePanel.Visibility = Visibility.Collapsed;
        CalibrateRunningPanel.Visibility = Visibility.Visible;
    }

    private void CalibrateTap_Click(object sender, RoutedEventArgs e)
    {
        _calibrator.RecordTap();
        CalibrateTapCountText.Text = $"{_calibrator.TapCount} of {CalibrationTapTarget} taps";

        if (_calibrator.TapCount < CalibrationTapTarget) return;

        var estimate = _calibrator.GetEstimatedDelayMs();
        _calibrator.Stop();
        CalibrateRunningPanel.Visibility = Visibility.Collapsed;

        if (estimate is double ms)
        {
            CalibrateResultText.Text = $"{ms:0}ms";
            CalibrateDonePanel.Visibility = Visibility.Visible;
        }
        else
        {
            CalibrateNotEnoughText.Visibility = Visibility.Visible;
            CalibrateIdlePanel.Visibility = Visibility.Visible;
        }
    }

    private void CalibrateCancel_Click(object sender, RoutedEventArgs e)
    {
        _calibrator.Stop();
        CalibratePopup.IsOpen = false;
    }

    private void CalibrateApply_Click(object sender, RoutedEventArgs e)
    {
        if (_calibratingDevice is not null)
        {
            var estimate = _calibrator.GetEstimatedDelayMs();
            if (estimate is double ms)
                _calibratingDevice.LatencyMs = ms;
        }

        _calibrator.Stop();
        CalibratePopup.IsOpen = false;
    }

    private void CalibratePopup_Closed(object sender, EventArgs e)
    {
        _calibrator.Stop();
        _calibratingDevice = null;
    }

    private void ShareToggle_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not AudioDeviceInfo device) return;

        GuestSharePopup.PlacementTarget = button;
        GuestShareDeviceName.Text = device.Name;

        if (_guestServer is not { IsRunning: true } server)
        {
            GuestShareUnavailableText.Visibility = Visibility.Visible;
            GuestShareQrImage.Source = null;
            GuestSharePinText.Text = "------";
        }
        else
        {
            GuestShareUnavailableText.Visibility = Visibility.Collapsed;
            var link = server.GetLinkForDevice(device.Id);
            GuestShareQrImage.Source = PngBytesToImage(server.GetQrPng(link));
            GuestSharePinText.Text = server.Pin;
        }

        GuestSharePopup.IsOpen = true;
    }

    private static System.Windows.Media.Imaging.BitmapImage PngBytesToImage(byte[] png)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage();
        using var stream = new System.IO.MemoryStream(png);
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
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
