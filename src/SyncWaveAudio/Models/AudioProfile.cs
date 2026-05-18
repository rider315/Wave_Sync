namespace SyncWaveAudio.Models;

public sealed class AudioProfile
{
    public string Name { get; set; } = "Default";
    public List<DeviceProfile> Devices { get; set; } = [];
    public string EqPreset { get; set; } = "Flat";
    public bool PartyMode { get; set; }
    public bool DebugLogging { get; set; }
}

public sealed class DeviceProfile
{
    public string DeviceId { get; set; } = string.Empty;
    public double ManualDelayMs { get; set; }
    public double Volume { get; set; } = 1.0;
    public bool Mono { get; set; }
    public bool EnhancementEnabled { get; set; } = true;
    public bool SpatialPreferred { get; set; }
}
