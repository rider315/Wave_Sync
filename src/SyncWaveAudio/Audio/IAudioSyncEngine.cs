using SyncWaveAudio.Models;

namespace SyncWaveAudio.Audio;

public interface IAudioSyncEngine
{
    event EventHandler<SyncSnapshot>? SnapshotReady;

    bool IsRunning { get; }
    Task StartAsync(IReadOnlyList<AudioDeviceInfo> devices, AudioDeviceInfo anchorDevice, CancellationToken cancellationToken = default);
    Task StopAsync();
    Task PlayCalibrationToneAsync(IReadOnlyList<AudioDeviceInfo> devices, CancellationToken cancellationToken = default);
}
