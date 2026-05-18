using System.IO;
using System.Text.Json;
using SyncWaveAudio.Models;

namespace SyncWaveAudio.Services;

public sealed class JsonProfileStore : IProfileStore
{
    private readonly string _profilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SyncWave Audio",
        "profile.json");

    public async Task<AudioProfile> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_profilePath))
        {
            return new AudioProfile();
        }

        await using var stream = File.OpenRead(_profilePath);
        return await JsonSerializer.DeserializeAsync<AudioProfile>(stream, cancellationToken: cancellationToken)
            ?? new AudioProfile();
    }

    public async Task SaveAsync(AudioProfile profile, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
        await using var stream = File.Create(_profilePath);
        await JsonSerializer.SerializeAsync(stream, profile, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }
}
