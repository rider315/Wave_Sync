namespace SyncWaveAudio.Devices;

public interface IBluetoothTelemetryService
{
    Task<BluetoothTelemetry> GetTelemetryAsync(string deviceName, CancellationToken cancellationToken = default);
}

public sealed record BluetoothTelemetry(
    bool IsBluetooth,
    int? BatteryPercent,
    int? SignalStrength,
    string Codec);
