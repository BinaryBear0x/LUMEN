using System.Text.Json;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;

namespace HuePC.Infrastructure.Persistence;

public sealed class JsonDeviceMemoryStore : IDeviceMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HuePC",
        "remembered-device.json");
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public async Task<RememberedBleDevice?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath)) return null;
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<RememberedBleDevice>(stream, JsonOptions, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(BleDeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        await _fileLock.WaitAsync(cancellationToken);
        var temporaryPath = $"{_filePath}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var remembered = new RememberedBleDevice(device, DateTimeOffset.UtcNow);
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, remembered, JsonOptions, cancellationToken);
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

    public async Task ForgetAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
