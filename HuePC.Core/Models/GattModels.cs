namespace HuePC.Core.Models;

[Flags]
public enum GattCharacteristicOperations
{
    None = 0,
    Broadcast = 1,
    Read = 2,
    WriteWithoutResponse = 4,
    Write = 8,
    Notify = 16,
    Indicate = 32,
    SignedWrites = 64,
    ExtendedProperties = 128
}

public sealed record GattAttributeKey(
    string ServiceUuid,
    int ServiceHandle,
    string CharacteristicUuid,
    int CharacteristicHandle);

public sealed record GattDescriptorSnapshot(
    string Uuid,
    string Name,
    int AttributeHandle,
    string ReadStatus,
    string? ValueHex,
    int? ValueByteLength,
    string? Error,
    long DurationMilliseconds);

public sealed record GattCharacteristicSnapshot(
    GattAttributeKey Key,
    string Name,
    GattCharacteristicOperations Operations,
    string ProtectionLevel,
    string ReadStatus,
    string? ValueHex,
    int? ValueByteLength,
    string? Error,
    long DurationMilliseconds,
    IReadOnlyList<GattDescriptorSnapshot> Descriptors);

public sealed record GattServiceSnapshot(
    string Uuid,
    string Name,
    string ServiceType,
    int AttributeHandle,
    string DiscoveryStatus,
    IReadOnlyList<string> IncludedServiceUuids,
    string? Error,
    long DurationMilliseconds,
    IReadOnlyList<GattCharacteristicSnapshot> Characteristics);

public sealed record GattDiscoverySnapshot(
    BleDeviceInfo Device,
    DateTimeOffset DiscoveredUtc,
    string ConnectionState,
    IReadOnlyList<GattServiceSnapshot> Services,
    IReadOnlyList<string> Errors);

public sealed record GattNotificationEvent(
    GattAttributeKey Key,
    DateTimeOffset TimestampUtc,
    string ValueHex,
    int ValueByteLength,
    bool IsIndication);

public sealed record GattOperationResult(
    bool IsSuccess,
    string Status,
    string? Error,
    long DurationMilliseconds);
