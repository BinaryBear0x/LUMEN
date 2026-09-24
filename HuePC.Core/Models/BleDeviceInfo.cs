namespace HuePC.Core.Models;

public sealed record ManufacturerDataEntry(ushort CompanyId, string DataHex);

public sealed record BleDeviceInfo(
    ulong BluetoothAddress,
    string Address,
    string AddressType,
    string Name,
    int Rssi,
    DateTimeOffset LastSeenUtc,
    IReadOnlyList<string> AdvertisedServiceUuids,
    IReadOnlyList<ManufacturerDataEntry> ManufacturerData,
    bool IsConnectable,
    bool IsHueCandidate)
{
    public string DeviceKey => BluetoothAddress.ToString("X12");
    public string? HueMatchReason { get; init; }
}

public sealed record AdapterStatus(
    bool IsAvailable,
    bool IsBluetoothEnabled,
    bool IsLowEnergySupported,
    string Description,
    string? Error = null);
