using System.Text.Json;
using HuePC.Core.Interfaces;

namespace HuePC.Infrastructure.Persistence;

public sealed class JsonDeviceAliasStore : IDeviceAliasStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private readonly string _filePath;
    private readonly string _legacyFilePath;

    public JsonDeviceAliasStore(string? storageDirectory = null)
    {
        var directory = storageDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HuePC");
        _filePath = Path.Combine(directory, "device-aliases.json");
        _legacyFilePath = Path.Combine(directory, "settings.json");
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_filePath))
            {
                await using var stream = File.OpenRead(_filePath);
                var document = await JsonSerializer.DeserializeAsync<SettingsDocument>(stream, JsonOptions, cancellationToken);
                return ToAliases(document?.DeviceAliases);
            }

            return await MigrateLegacyAliasesAsync(cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, string> aliases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        await FileLock.WaitAsync(cancellationToken);
        var temporaryPath = $"{_filePath}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            await WriteAliasesAsync(aliases, temporaryPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            FileLock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> MigrateLegacyAliasesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_legacyFilePath))
        {
            return ToAliases(null);
        }

        await using var stream = File.OpenRead(_legacyFilePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !TryGetPropertyCaseInsensitive(document.RootElement, nameof(SettingsDocument.DeviceAliases), out var aliasesElement) ||
            aliasesElement.ValueKind != JsonValueKind.Object)
        {
            return ToAliases(null);
        }

        var aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(aliasesElement.GetRawText(), JsonOptions);
        var result = ToAliases(aliases);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await WriteAliasesAsync(result, $"{_filePath}.tmp", cancellationToken);
        return result;
    }

    private async Task WriteAliasesAsync(
        IReadOnlyDictionary<string, string> aliases,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        var document = new SettingsDocument(aliases.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static Dictionary<string, string> ToAliases(Dictionary<string, string>? aliases) =>
        aliases is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(aliases, StringComparer.OrdinalIgnoreCase);

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed record SettingsDocument(Dictionary<string, string> DeviceAliases);
}
