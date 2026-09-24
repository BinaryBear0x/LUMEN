namespace HuePC.Core.Models;

/// <summary>
/// Device details read from the bulb: standard Device Information service values, the Zigbee
/// address reported by the Hue configuration service and the bulb's own user defined name.
/// </summary>
public sealed record HueDeviceInfo(
    string? Model,
    string? SoftwareVersion,
    string? Manufacturer,
    string? ZigbeeAddress,
    string? DeviceName);
