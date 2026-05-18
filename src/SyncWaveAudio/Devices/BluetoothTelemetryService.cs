using Microsoft.Extensions.Logging;

namespace SyncWaveAudio.Devices;

public sealed class BluetoothTelemetryService(ILogger<BluetoothTelemetryService> logger) : IBluetoothTelemetryService
{
    public Task<BluetoothTelemetry> GetTelemetryAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        var lower = deviceName.ToLowerInvariant();
        var looksBluetooth = lower.Contains("bluetooth", StringComparison.Ordinal)
            || lower.Contains("headphones", StringComparison.Ordinal)
            || lower.Contains("buds", StringComparison.Ordinal)
            || lower.Contains("a2dp", StringComparison.Ordinal)
            || lower.Contains("stereo", StringComparison.Ordinal);

        try
        {
            var codec = looksBluetooth ? InferCodec(lower) : "PCM";
            return Task.FromResult(new BluetoothTelemetry(looksBluetooth, null, null, codec));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to read Bluetooth telemetry for {DeviceName}", deviceName);
            return Task.FromResult(new BluetoothTelemetry(looksBluetooth, null, null, "Unknown"));
        }
    }

    private static string InferCodec(string deviceName)
    {
        if (deviceName.Contains("aptx", StringComparison.Ordinal)) return "aptX";
        if (deviceName.Contains("ldac", StringComparison.Ordinal)) return "LDAC";
        if (deviceName.Contains("aac", StringComparison.Ordinal)) return "AAC";
        return "SBC/A2DP";
    }
}
