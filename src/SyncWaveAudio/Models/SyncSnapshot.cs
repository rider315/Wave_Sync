namespace SyncWaveAudio.Models;

public sealed record SyncSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<DeviceSyncState> Devices,
    float PeakLeft,
    float PeakRight,
    bool IsRunning);

public sealed record DeviceSyncState(
    string DeviceId,
    double EstimatedLatencyMs,
    double ManualDelayMs,
    double EffectiveDelayMs,
    double DriftMs,
    int BufferedMilliseconds,
    string Status);
