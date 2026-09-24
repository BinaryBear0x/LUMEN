using System.Text.Json;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;

namespace HuePC.Infrastructure.Persistence;

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private readonly string _filePath;

    public JsonAppSettingsStore(string? storageDirectory = null)
    {
        _filePath = Path.Combine(
            storageDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuePC"),
            "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken) ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await FileLock.WaitAsync(cancellationToken);
        var temporaryPath = $"{_filePath}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            FileLock.Release();
        }
    }
}
