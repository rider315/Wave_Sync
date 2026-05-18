using Microsoft.Extensions.Configuration;

namespace SyncWaveAudio.Services;

public sealed class AppSettings
{
    public int DefaultBufferMilliseconds { get; }
    public int MinimumBufferMilliseconds { get; }
    public int MaximumManualDelayMilliseconds { get; }
    public int DriftCorrectionIntervalMilliseconds { get; }
    public bool EnableDebugLogging { get; }

    public AppSettings(IConfiguration configuration)
    {
        var section = configuration.GetSection("SyncWaveAudio");
        DefaultBufferMilliseconds = section.GetValue("DefaultBufferMilliseconds", 120);
        MinimumBufferMilliseconds = section.GetValue("MinimumBufferMilliseconds", 35);
        MaximumManualDelayMilliseconds = section.GetValue("MaximumManualDelayMilliseconds", 500);
        DriftCorrectionIntervalMilliseconds = section.GetValue("DriftCorrectionIntervalMilliseconds", 250);
        EnableDebugLogging = section.GetValue("EnableDebugLogging", true);
    }
}
