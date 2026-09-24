namespace HuePC.Core.Models;

public enum CapabilityState
{
    Unknown,
    Supported,
    Unsupported
}

public sealed record DeviceCapability(
    string Name,
    CapabilityState State,
    string Explanation,
    string? Evidence = null);

public sealed record CapabilityReport(DateTimeOffset GeneratedUtc, IReadOnlyList<DeviceCapability> Capabilities);
