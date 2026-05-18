namespace SyncWaveAudio.Models;

public enum LogLevel
{
    Info,
    Warning,
    Error
}

public enum LogCategory
{
    Capture,
    Buffer,
    Drift,
    Sync,
    Device,
    System
}

public sealed class SyncLogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string DeviceName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public LogCategory Category { get; init; } = LogCategory.System;

    public string FormattedTime => Timestamp.ToString("HH:mm:ss.fff");
    public string LevelTag => Level switch
    {
        LogLevel.Warning => "⚠",
        LogLevel.Error => "✖",
        _ => "ℹ"
    };
}
