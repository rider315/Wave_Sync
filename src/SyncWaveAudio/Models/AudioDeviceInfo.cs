using CommunityToolkit.Mvvm.ComponentModel;

namespace SyncWaveAudio.Models;

public partial class AudioDeviceInfo : ObservableObject
{
    [ObservableProperty] private string id = string.Empty;
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string friendlyName = string.Empty;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isDefaultOutput;
    [ObservableProperty] private bool isBluetooth;
    [ObservableProperty] private int? batteryPercent;
    [ObservableProperty] private int? signalStrength;
    [ObservableProperty] private string codec = "Unknown";
    [ObservableProperty] private double estimatedLatencyMs;
    [ObservableProperty] private double manualDelayMs;
    [ObservableProperty] private double driftMs;
    [ObservableProperty] private double volume = 1.0;
    [ObservableProperty] private bool mono;
    [ObservableProperty] private bool enhancementEnabled = true;
    [ObservableProperty] private bool spatialPreferred;
    [ObservableProperty] private bool isConnected = true;
    [ObservableProperty] private string deviceState = "Active";
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private float[]? waveformSamples;

    public bool IsAnchorDevice => IsDefaultOutput;
    public bool IsRelayOutput => !IsDefaultOutput;
    public bool IsSelectableOutput => IsConnected;
    public string DeviceRoleLabel => IsDefaultOutput ? "Source / Anchor" : IsConnected ? "Relay Output" : "Paired / Disconnected";

    partial void OnIsDefaultOutputChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAnchorDevice));
        OnPropertyChanged(nameof(IsRelayOutput));
        OnPropertyChanged(nameof(DeviceRoleLabel));
    }

    partial void OnIsConnectedChanged(bool value)
    {
        if (!value)
        {
            IsSelected = false;
        }

        OnPropertyChanged(nameof(IsSelectableOutput));
        OnPropertyChanged(nameof(DeviceRoleLabel));
    }
}
