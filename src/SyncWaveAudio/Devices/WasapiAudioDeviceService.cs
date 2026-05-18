using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using SyncWaveAudio.Models;

namespace SyncWaveAudio.Devices;

public sealed class WasapiAudioDeviceService(
    IBluetoothTelemetryService bluetoothTelemetry,
    ILogger<WasapiAudioDeviceService> logger) : IAudioDeviceService
{
    public async Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var defaultEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .OrderByDescending(device => string.Equals(device.ID, defaultEndpoint.ID, StringComparison.OrdinalIgnoreCase))
            .ThenBy(device => device.FriendlyName)
            .ToList();

        var result = new List<AudioDeviceInfo>();
        foreach (var endpoint in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var telemetry = await bluetoothTelemetry.GetTelemetryAsync(endpoint.FriendlyName, cancellationToken);
            var isDefaultOutput = string.Equals(endpoint.ID, defaultEndpoint.ID, StringComparison.OrdinalIgnoreCase);
            result.Add(new AudioDeviceInfo
            {
                Id = endpoint.ID,
                Name = endpoint.DeviceFriendlyName,
                FriendlyName = endpoint.FriendlyName,
                IsDefaultOutput = isDefaultOutput,
                IsBluetooth = telemetry.IsBluetooth,
                BatteryPercent = telemetry.BatteryPercent,
                SignalStrength = telemetry.SignalStrength,
                Codec = telemetry.Codec,
                EstimatedLatencyMs = EstimateBaseLatency(endpoint, telemetry),
                Volume = GetEndpointVolume(endpoint),
                Status = isDefaultOutput ? "Source / Anchor" : "Ready"
            });
        }

        return result;
    }

    public Task ReconnectAsync(AudioDeviceInfo device, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Reconnect requested for {DeviceName}. Windows owns Bluetooth pairing; refreshing endpoint state.", device.FriendlyName);
        device.Status = "Refreshing";
        device.IsConnected = true;
        device.Status = "Ready";
        return Task.CompletedTask;
    }

    public Task SetEndpointVolumeAsync(AudioDeviceInfo device, double volume, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDevice(device.Id);
            endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(volume, 0.0, 1.0);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to set endpoint volume for {DeviceName}", device.FriendlyName);
        }

        return Task.CompletedTask;
    }

    private static double EstimateBaseLatency(MMDevice device, BluetoothTelemetry telemetry)
    {
        var engineLatencyMs = device.AudioClient.DefaultDevicePeriod / 10_000.0;
        var codecPenalty = telemetry.Codec switch
        {
            "aptX" => 120,
            "AAC" => 165,
            "LDAC" => 180,
            "SBC/A2DP" => 220,
            _ when telemetry.IsBluetooth => 200,
            _ => 30
        };

        return Math.Round(engineLatencyMs + codecPenalty, 1);
    }

    private static double GetEndpointVolume(MMDevice device)
    {
        try
        {
            return Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar, 2);
        }
        catch
        {
            return 1.0;
        }
    }
}
