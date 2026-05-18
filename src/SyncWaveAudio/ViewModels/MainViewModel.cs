using System.Collections.ObjectModel;
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

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];
    public IReadOnlyList<string> EqPresets { get; } = ["Flat", "Warm", "Vocal Lift", "Bass Control", "Night"];

    public MainViewModel(
        IAudioDeviceService deviceService,
        IAudioSyncEngine syncEngine,
        IProfileStore profileStore,
        ILogger<MainViewModel> logger)
    {
        _deviceService = deviceService;
        _syncEngine = syncEngine;
        _profileStore = profileStore;
        _logger = logger;
        _syncEngine.SnapshotReady += OnSnapshotReady;
    }

    public int SelectedDeviceCount => Devices.Count(device => device.IsSelected);
    public int SelectedRelayDeviceCount => Devices.Count(device => device.IsSelected && device.IsRelayOutput);
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to refresh devices.");
            StatusText = ex.Message;
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

            var selected = Devices.Where(device => device.IsSelected && device.IsRelayOutput).ToList();
            if (selected.Count == 0)
            {
                StatusText = "Select your current speaker if you want, plus at least one extra relay device.";
                return;
            }

            await _syncEngine.StartAsync(selected);
            IsRunning = true;
            StatusText = $"Streaming to {selected.Count} relay device(s). Selected anchor plays normally through Windows.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to start synchronized routing.");
            StatusText = ex.Message;
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
        foreach (var device in Devices)
        {
            if (device.IsSelected)
            {
                device.Status = "Ready";
            }
        }

        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task CalibrateAsync()
    {
        var selected = Devices.Where(device => device.IsSelected && device.IsRelayOutput).ToList();
        if (selected.Count == 0)
        {
            StatusText = "Select at least one relay device before calibration";
            return;
        }

        StatusText = "Playing calibration sweep";
        await _syncEngine.PlayCalibrationToneAsync(selected);

        var maxLatency = selected.Max(device => device.EstimatedLatencyMs);
        foreach (var device in selected)
        {
            device.ManualDelayMs = Math.Round(maxLatency - device.EstimatedLatencyMs, 1);
            device.Status = "Calibrated";
        }

        StatusText = "Calibration profile updated";
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

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedEqPresetChanged(string value) => _ = SaveProfileAsync();
    partial void OnPartyModeChanged(bool value) => _ = SaveProfileAsync();
    partial void OnDebugLoggingChanged(bool value) => _ = SaveProfileAsync();

    private void OnSnapshotReady(object? sender, SyncSnapshot snapshot)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            PeakLeft = snapshot.PeakLeft;
            PeakRight = snapshot.PeakRight;
            IsRunning = snapshot.IsRunning;

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
                device.Status = Math.Abs(state.DriftMs) < 20 ? "Locked" : "Correcting";
            }
        });
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
