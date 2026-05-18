using SyncWaveAudio.Models;

namespace SyncWaveAudio.Devices;

public interface IAudioDeviceService
{
    Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default);
    Task ReconnectAsync(AudioDeviceInfo device, CancellationToken cancellationToken = default);
    Task SetEndpointVolumeAsync(AudioDeviceInfo device, double volume, CancellationToken cancellationToken = default);
}
