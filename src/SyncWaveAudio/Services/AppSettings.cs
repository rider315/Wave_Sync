using Microsoft.Extensions.Configuration;

namespace SyncWaveAudio.Services;

public sealed class AppSettings
{
    private int _defaultBufferMilliseconds;

    public int DefaultBufferMilliseconds
    {
        get => _defaultBufferMilliseconds;
        set => _defaultBufferMilliseconds = Math.Clamp(value, MinimumBufferMilliseconds, 500);
    }

    public int MinimumBufferMilliseconds { get; }
    public int MaximumManualDelayMilliseconds { get; }
    public int DriftCorrectionIntervalMilliseconds { get; }
    public bool EnableDebugLogging { get; set; }

    public AppSettings(IConfiguration configuration)
    {
        var section = configuration.GetSection("SyncWaveAudio");
        MinimumBufferMilliseconds = section.GetValue("MinimumBufferMilliseconds", 35);
        _defaultBufferMilliseconds = section.GetValue("DefaultBufferMilliseconds", 80);
        MaximumManualDelayMilliseconds = section.GetValue("MaximumManualDelayMilliseconds", 500);
        DriftCorrectionIntervalMilliseconds = section.GetValue("DriftCorrectionIntervalMilliseconds", 150);
        EnableDebugLogging = section.GetValue("EnableDebugLogging", true);
    }
}
