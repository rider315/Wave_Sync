namespace SyncWaveAudio.Models;

public sealed record SyncSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<DeviceSyncState> Devices,
    float PeakLeft,
    float PeakRight,
    bool IsRunning,
    double MaxDriftMs = 0,
    double AvgDriftMs = 0,
    int SyncHealthPercent = 100,
    double CaptureCallbackIntervalMs = 0,
    long TotalCaptureCallbacks = 0,
    long TotalBytesCaptur = 0,
    float[]? WaveformSamples = null);

public sealed record DeviceSyncState(
    string DeviceId,
    string DeviceName,
    double EstimatedLatencyMs,
    double ManualDelayMs,
    double EffectiveDelayMs,
    double DriftMs,
    int BufferedMilliseconds,
    string Status,
    float[]? WaveformSamples = null,
    long TotalTrimmedBytes = 0,
    long TotalSilenceBytes = 0,
    long TotalOverflows = 0);
