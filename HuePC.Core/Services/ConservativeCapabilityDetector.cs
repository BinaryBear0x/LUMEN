using HuePC.Core.Interfaces;
using HuePC.Core.Models;

namespace HuePC.Core.Services;

/// <summary>
/// Generic GATT properties do not identify the vendor-specific meaning of a light command.
/// A feature is marked supported only when the exact Hue characteristic that was verified on the
/// target bulb (model LCA016, firmware 1.126.9, encrypted bond) is present in the discovery.
/// </summary>
public sealed class ConservativeCapabilityDetector : ICapabilityDetector
{
    private static readonly (string Name, string CharacteristicUuid)[] VerifiedCapabilities =
    [
        ("Power", HueLightControlProtocol.PowerCharacteristicUuid),
        ("Brightness", HueLightControlProtocol.BrightnessCharacteristicUuid),
        ("Color", HueLightControlProtocol.ColorCharacteristicUuid),
        ("Color temperature", HueLightControlProtocol.ColorTemperatureCharacteristicUuid)
    ];

    public CapabilityReport Detect(GattDiscoverySnapshot discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        var observed = discovery.Services
            .SelectMany(service => service.Characteristics)
            .Select(characteristic => characteristic.Key.CharacteristicUuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var capabilities = VerifiedCapabilities
            .Select(capability => observed.Contains(capability.CharacteristicUuid)
                ? new DeviceCapability(
                    capability.Name,
                    CapabilityState.Supported,
                    "Hue protokol eşlemesi hedef ampulde doğrulandı (LCA016, yazılım 1.126.9).",
                    $"Characteristic {capability.CharacteristicUuid} şifreli bağ üzerinden yazma/okuma ile doğrulandı.")
                : new DeviceCapability(
                    capability.Name,
                    CapabilityState.Unknown,
                    "Bu cihaz için doğrulanmış bir ışık protokolü eşlemesi henüz yok."))
            .ToArray();

        return new CapabilityReport(DateTimeOffset.UtcNow, capabilities);
    }
}
