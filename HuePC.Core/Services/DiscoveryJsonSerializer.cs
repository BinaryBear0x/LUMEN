using System.Text.Json;
using HuePC.Core.Models;

namespace HuePC.Core.Services;

public static class DiscoveryJsonSerializer
{
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(DiscoveryExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, ExportOptions);
    }
}
