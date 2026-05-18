using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SyncWaveAudio.Audio;
using SyncWaveAudio.Devices;
using SyncWaveAudio.Models;
using SyncWaveAudio.Services;

namespace SyncWaveAudio.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAudioDeviceService _deviceService;
    private readonly IAudioSyncEngine _syncEngine;
    private readonly IProfileStore _profileStore;
    private readonly ILogger<MainViewModel> _logger;
    private readonly AppSettings _settings;
    private AudioProfile _profile = new();

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusText = "Ready";
    [ObservableProperty] private string selectedEqPreset = "Flat";
    [ObservableProperty] private bool partyMode;
    [ObservableProperty] private bool debugLogging = true;
    [ObservableProperty] private float peakLeft;
    [ObservableProperty] private float peakRight;
    [ObservableProperty] private string activeTab = "Devices";

    // Live sync health properties
    [ObservableProperty] private int syncHealthPercent = 100;
    [ObservableProperty] private double maxDriftMs;
    [ObservableProperty] private double avgDriftMs;
    [ObservableProperty] private string syncHealthLabel = "IDLE";
    [ObservableProperty] private string syncHealthColor = "#9AA7B8";
    [ObservableProperty] private double captureCallbackIntervalMs;
    [ObservableProperty] private long totalCaptureCallbacks;

    // Buffer size slider (user adjustable in real-time)
    [ObservableProperty] private int bufferSizeMs = 80;

    // Debug log panel
    [ObservableProperty] private bool isDebugPanelOpen = true;

    // Log capture
    [ObservableProperty] private bool isCapturing;
    [ObservableProperty] private int captureCountdown;
    private readonly List<SyncLogEntry> _captureBuffer = [];
    private Timer? _captureTimer;

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];
    public ObservableCollection<SyncLogEntry> DebugLogs { get; } = [];
    public IReadOnlyList<string> EqPresets { get; } = ["Flat", "Warm", "Vocal Lift", "Bass Control", "Night"];

    public MainViewModel(
        IAudioDeviceService deviceService,
        IAudioSyncEngine syncEngine,
        IProfileStore profileStore,
        ILogger<MainViewModel> logger,
        AppSettings settings)
    {
        _deviceService = deviceService;
        _syncEngine = syncEngine;
        _profileStore = profileStore;
        _logger = logger;
        _settings = settings;
        bufferSizeMs = settings.DefaultBufferMilliseconds;
        _syncEngine.SnapshotReady += OnSnapshotReady;

        // Subscribe to log events from the sync engine
        if (_syncEngine is WasapiAudioSyncEngine wasapiEngine)
        {
            wasapiEngine.LogEmitted += OnLogEmitted;
        }
    }

    public int SelectedDeviceCount => Devices.Count(device => device.IsSelected);
    public int SelectedRelayDeviceCount => Devices.Count(device => device.IsSelected && device.IsRelayOutput && device.IsConnected);
    public bool CanStart => !IsRunning && SelectedRelayDeviceCount >= 1;

    [RelayCommand]
    private async Task InitializeAsync()
    {
        await RefreshDevicesAsync();
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        IsBusy = true;
        StatusText = "Scanning Windows audio endpoints";
        try
        {
            _profile = await _profileStore.LoadAsync();
            SelectedEqPreset = _profile.EqPreset;
            PartyMode = _profile.PartyMode;
            DebugLogging = _profile.DebugLogging;

            var devices = await _deviceService.GetOutputDevicesAsync();
            Devices.Clear();
            foreach (var device in devices)
            {
                ApplyProfile(device);

                device.PropertyChanged += (_, args) =>
                {
                    if (!device.IsConnected && device.IsSelected)
                    {
                        device.IsSelected = false;
                    }

                    if (args.PropertyName == nameof(AudioDeviceInfo.Volume))
                    {
                        _ = _deviceService.SetEndpointVolumeAsync(device, device.Volume);
                    }

                    OnPropertyChanged(nameof(SelectedDeviceCount));
                    OnPropertyChanged(nameof(SelectedRelayDeviceCount));
                    OnPropertyChanged(nameof(CanStart));
                    StartCommand.NotifyCanExecuteChanged();
                    _ = SaveProfileAsync();
                };
                Devices.Add(device);
            }

            StatusText = Devices.Count == 0 ? "No active output devices found" : $"Found {Devices.Count} output devices";
            AddLog(LogCategory.System, $"Scanned {Devices.Count} output devices");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to refresh devices.");
            StatusText = ex.Message;
            AddLog(LogCategory.System, $"Refresh failed: {ex.Message}", Models.LogLevel.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        StatusText = "Starting synchronized WASAPI routing";
        try
        {
            await RefreshAnchorRolesAsync();
            foreach (var device in Devices.Where(device => device.IsSelected))
            {
                await _deviceService.SetEndpointVolumeAsync(device, device.Volume);
            }

            var selected = Devices.Where(device => device.IsSelected && device.IsRelayOutput && device.IsConnected).ToList();
            if (selected.Count == 0)
            {
                StatusText = "Select your current speaker if you want, plus at least one extra relay device.";
                return;
            }

            var anchor = Devices.FirstOrDefault(device => device.IsAnchorDevice);
            if (anchor is null)
            {
                StatusText = "Set a Windows default output device first, then refresh SyncWave.";
                return;
            }

            // Apply current buffer size to settings before starting
            _settings.DefaultBufferMilliseconds = BufferSizeMs;
            AddLog(LogCategory.System, $"Starting sync — buffer={BufferSizeMs}ms, devices={selected.Count}");

            await _syncEngine.StartAsync(selected, anchor);
            IsRunning = true;
            StatusText = $"Auto-stabilizing {selected.Count} relay device(s) against {anchor.FriendlyName}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to start synchronized routing.");
            StatusText = ex.Message;
            AddLog(LogCategory.System, $"Start failed: {ex.Message}", Models.LogLevel.Error);
        }
        finally
        {
            IsBusy = false;
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await _syncEngine.StopAsync();
        IsRunning = false;
        StatusText = "Stopped";
        SyncHealthLabel = "IDLE";
        SyncHealthColor = "#9AA7B8";
        SyncHealthPercent = 100;
        MaxDriftMs = 0;
        AvgDriftMs = 0;
        foreach (var device in Devices)
        {
            if (device.IsSelected)
            {
                device.Status = "Ready";
            }
        }

        StartCommand.NotifyCanExecuteChanged();
        AddLog(LogCategory.System, "Sync stopped");
    }

    [RelayCommand]
    private async Task CalibrateAsync()
    {
        var selected = Devices.Where(device => device.IsSelected && device.IsRelayOutput && device.IsConnected).ToList();
        if (selected.Count == 0)
        {
            StatusText = "Select at least one relay device before calibration";
            return;
        }

        StatusText = "Playing calibration sweep";
        AddLog(LogCategory.Sync, "Calibration sweep started");
        await _syncEngine.PlayCalibrationToneAsync(selected);

        var anchor = Devices.FirstOrDefault(device => device.IsAnchorDevice);
        if (anchor is null)
        {
            StatusText = "Set a Windows default output device first, then refresh SyncWave.";
            return;
        }

        foreach (var device in selected)
        {
            device.ManualDelayMs = Math.Round(Math.Max(0, anchor.EstimatedLatencyMs - device.EstimatedLatencyMs), 1);
            device.Status = device.ManualDelayMs > 0 ? "Anchor calibrated" : "Use delay if this device is late";
        }

        StatusText = "Calibration profile updated";
        AddLog(LogCategory.Sync, "Calibration complete — delay offsets updated");
        await SaveProfileAsync();
    }

    [RelayCommand]
    private async Task ReconnectAsync(AudioDeviceInfo? device)
    {
        if (device is null)
        {
            return;
        }

        await _deviceService.ReconnectAsync(device);
        await RefreshDevicesAsync();
    }

    [RelayCommand]
    private void MoveDeviceUp(AudioDeviceInfo? device)
    {
        if (device is null)
        {
            return;
        }

        var index = Devices.IndexOf(device);
        if (index > 0)
        {
            Devices.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveDeviceDown(AudioDeviceInfo? device)
    {
        if (device is null)
        {
            return;
        }

        var index = Devices.IndexOf(device);
        if (index >= 0 && index < Devices.Count - 1)
        {
            Devices.Move(index, index + 1);
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        DebugLogs.Clear();
    }

    [RelayCommand]
    private void ToggleDebugPanel()
    {
        IsDebugPanelOpen = !IsDebugPanelOpen;
    }

    [RelayCommand]
    private void CaptureLogs()
    {
        if (IsCapturing) return;

        IsCapturing = true;
        CaptureCountdown = 10;
        _captureBuffer.Clear();
        AddLog(LogCategory.System, "📸 Log capture started — recording for 10 seconds...");

        _captureTimer = new Timer(_ =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                CaptureCountdown--;
                if (CaptureCountdown <= 0)
                {
                    _captureTimer?.Dispose();
                    _captureTimer = null;
                    SaveCapturedLogs();
                    IsCapturing = false;
                }
            });
        }, null, 1000, 1000);
    }

    private void SaveCapturedLogs()
    {
        try
        {
            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SyncWave Audio", "logs");
            Directory.CreateDirectory(logsDir);

            var fileName = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            var filePath = Path.Combine(logsDir, fileName);

            var sb = new StringBuilder();
            sb.AppendLine($"=== SyncWave Audio Log Capture ===");
            sb.AppendLine($"Captured at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Buffer size: {BufferSizeMs}ms");
            sb.AppendLine($"Running: {IsRunning}");
            sb.AppendLine($"Devices: {SelectedDeviceCount} selected, {SelectedRelayDeviceCount} relay");
            sb.AppendLine($"Sync health: {SyncHealthPercent}% ({SyncHealthLabel})");
            sb.AppendLine($"Max drift: {MaxDriftMs:F1}ms, Avg drift: {AvgDriftMs:F1}ms");
            sb.AppendLine($"Entries: {_captureBuffer.Count}");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();

            foreach (var entry in _captureBuffer)
            {
                var device = string.IsNullOrEmpty(entry.DeviceName) ? "" : $" [{entry.DeviceName}]";
                sb.AppendLine($"{entry.FormattedTime} {entry.LevelTag} {entry.Category,-8}{device} {entry.Message}");
            }

            File.WriteAllText(filePath, sb.ToString());
            AddLog(LogCategory.System, $"📁 Logs saved: {filePath} ({_captureBuffer.Count} entries)");
            StatusText = $"Logs saved to {fileName}";
        }
        catch (Exception ex)
        {
            AddLog(LogCategory.System, $"Failed to save logs: {ex.Message}", Models.LogLevel.Error);
        }
        finally
        {
            _captureBuffer.Clear();
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedEqPresetChanged(string value) => _ = SaveProfileAsync();
    partial void OnPartyModeChanged(bool value) => _ = SaveProfileAsync();

    partial void OnDebugLoggingChanged(bool value)
    {
        _settings.EnableDebugLogging = value;
        _ = SaveProfileAsync();
    }

    partial void OnBufferSizeMsChanged(int value)
    {
        _settings.DefaultBufferMilliseconds = value;
        AddLog(LogCategory.System, $"Buffer size changed to {value}ms");
    }

    private void OnSnapshotReady(object? sender, SyncSnapshot snapshot)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            PeakLeft = snapshot.PeakLeft;
            PeakRight = snapshot.PeakRight;
            IsRunning = snapshot.IsRunning;

            // Update sync health metrics
            SyncHealthPercent = snapshot.SyncHealthPercent;
            MaxDriftMs = snapshot.MaxDriftMs;
            AvgDriftMs = snapshot.AvgDriftMs;
            CaptureCallbackIntervalMs = snapshot.CaptureCallbackIntervalMs;
            TotalCaptureCallbacks = snapshot.TotalCaptureCallbacks;

            // Color-coded sync status
            if (snapshot.MaxDriftMs < 5)
            {
                SyncHealthLabel = "LOCKED";
                SyncHealthColor = "#3DD6B3"; // Green/accent
            }
            else if (snapshot.MaxDriftMs < 15)
            {
                SyncHealthLabel = "CORRECTING";
                SyncHealthColor = "#FFB84D"; // Yellow/warning
            }
            else
            {
                SyncHealthLabel = "DRIFTING";
                SyncHealthColor = "#FF667A"; // Red/danger
            }

            foreach (var state in snapshot.Devices)
            {
                var device = Devices.FirstOrDefault(item => item.Id == state.DeviceId);
                if (device is null)
                {
                    continue;
                }

                device.EstimatedLatencyMs = state.EstimatedLatencyMs;
                device.ManualDelayMs = state.ManualDelayMs;
                device.DriftMs = state.DriftMs;
                device.Status = Math.Abs(state.DriftMs) < 3 ? "Locked" : "Correcting";
            }
        });
    }

    private void OnLogEmitted(object? sender, SyncLogEntry entry)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            AddLogEntry(entry);
        });
    }

    private void AddLog(LogCategory category, string message, Models.LogLevel level = Models.LogLevel.Info, string deviceName = "")
    {
        AddLogEntry(new SyncLogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            Category = category,
            DeviceName = deviceName,
            Message = message
        });
    }

    private void AddLogEntry(SyncLogEntry entry)
    {
        DebugLogs.Add(entry);
        while (DebugLogs.Count > 300)
        {
            DebugLogs.RemoveAt(0);
        }

        // Feed to capture buffer if capturing
        if (IsCapturing)
        {
            _captureBuffer.Add(entry);
        }
    }

    private void ApplyProfile(AudioDeviceInfo device)
    {
        var profile = _profile.Devices.FirstOrDefault(item => item.DeviceId == device.Id);
        if (profile is null)
        {
            return;
        }

        device.ManualDelayMs = profile.ManualDelayMs;
        device.Volume = profile.Volume;
        device.Mono = profile.Mono;
        device.EnhancementEnabled = profile.EnhancementEnabled;
        device.SpatialPreferred = profile.SpatialPreferred;
    }

    private async Task RefreshAnchorRolesAsync()
    {
        var latestDevices = await _deviceService.GetOutputDevicesAsync();
        var anchorIds = latestDevices
            .Where(device => device.IsAnchorDevice)
            .Select(device => device.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var device in Devices)
        {
            device.IsDefaultOutput = anchorIds.Contains(device.Id);
            if (device.IsAnchorDevice)
            {
                device.Status = "Source / Anchor";
            }
            else if (!device.IsConnected)
            {
                device.Status = "Connect in Windows, then refresh";
            }
        }

        OnPropertyChanged(nameof(SelectedDeviceCount));
        OnPropertyChanged(nameof(SelectedRelayDeviceCount));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    private async Task SaveProfileAsync()
    {
        try
        {
            _profile.EqPreset = SelectedEqPreset;
            _profile.PartyMode = PartyMode;
            _profile.DebugLogging = DebugLogging;
            _profile.Devices = Devices.Select(device => new DeviceProfile
            {
                DeviceId = device.Id,
                ManualDelayMs = device.ManualDelayMs,
                Volume = device.Volume,
                Mono = device.Mono,
                EnhancementEnabled = device.EnhancementEnabled,
                SpatialPreferred = device.SpatialPreferred
            }).ToList();

            await _profileStore.SaveAsync(_profile);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to save profile.");
        }
    }
}
