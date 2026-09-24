namespace HuePC.Core.Models;

public sealed record DiscoveryExportDocument(
    int SchemaVersion,
    DateTimeOffset ExportedUtc,
    string Application,
    AdapterStatus Adapter,
    GattDiscoverySnapshot Discovery,
    CapabilityReport? Capabilities);
