using System.Text.Json;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;

namespace HuePC.Infrastructure.Persistence;

public sealed class JsonScheduleStore : IScheduleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HuePC",
        "schedules.json");
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public async Task<IReadOnlyList<LightSchedule>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<LightSchedule>>(stream, JsonOptions, cancellationToken) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<LightSchedule> schedules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        await _fileLock.WaitAsync(cancellationToken);
        var temporaryPath = $"{_filePath}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, schedules, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            _fileLock.Release();
        }
    }
}
