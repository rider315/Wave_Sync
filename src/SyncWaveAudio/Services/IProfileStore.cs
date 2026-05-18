using SyncWaveAudio.Models;

namespace SyncWaveAudio.Services;

public interface IProfileStore
{
    Task<AudioProfile> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AudioProfile profile, CancellationToken cancellationToken = default);
}
