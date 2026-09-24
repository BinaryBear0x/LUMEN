namespace HuePC.Core.Services;

/// <summary>
/// Verified Philips Hue BLE light control mapping, confirmed on the target bulb
/// (model LCA016, firmware 1.126.9) over an encrypted Windows bond:
/// power, brightness, color temperature, color and identify all accept acknowledged writes and
/// report their state through notifications. Without a bond the ATT layer rejects these
/// attributes with "Insufficient Authentication" (0x05), so the bulb must be paired first.
/// </summary>
public static class HueLightControlProtocol
{
    public const string LightControlServiceUuid = "932C32BD-0000-47A2-835A-A8D455B859DD";
    public const string PowerCharacteristicUuid = "932C32BD-0002-47A2-835A-A8D455B859DD";
    public const string BrightnessCharacteristicUuid = "932C32BD-0003-47A2-835A-A8D455B859DD";
    public const string ColorTemperatureCharacteristicUuid = "932C32BD-0004-47A2-835A-A8D455B859DD";
    public const string ColorCharacteristicUuid = "932C32BD-0005-47A2-835A-A8D455B859DD";
    public const string IdentifyCharacteristicUuid = "932C32BD-0006-47A2-835A-A8D455B859DD";
    public const string CombinedStateCharacteristicUuid = "932C32BD-0007-47A2-835A-A8D455B859DD";
    public const string PowerOnBehaviourCharacteristicUuid = "932C32BD-1005-47A2-835A-A8D455B859DD";

    public const string HueConfigurationServiceUuid = "0000FE0F-0000-1000-8000-00805F9B34FB";
    public const string ZigbeeAddressCharacteristicUuid = "97FE6561-0001-4F62-86E9-B71EE2DA3D22";
    public const string DeviceNameCharacteristicUuid = "97FE6561-0003-4F62-86E9-B71EE2DA3D22";
    public const string DeviceInformationServiceUuid = "0000180A-0000-1000-8000-00805F9B34FB";
    public const string ManufacturerNameCharacteristicUuid = "00002A29-0000-1000-8000-00805F9B34FB";
    public const string ModelNumberCharacteristicUuid = "00002A24-0000-1000-8000-00805F9B34FB";
    public const string SoftwareRevisionCharacteristicUuid = "00002A28-0000-1000-8000-00805F9B34FB";

    public const int DeviceNameMaximumCharacters = 32;

    public const byte PowerOnValue = 0x01;
    public const byte PowerOffValue = 0x00;
    public const byte BrightnessMinimum = 0x01;
    public const byte BrightnessMaximum = 0xFE;
    public const ushort ColorTemperatureMinimumMired = 154;
    public const ushort ColorTemperatureMaximumMired = 455;

    /// <summary>
    /// Payloads for the power-up control characteristic (932C32BD-1005). 0xFF is the sigil that
    /// tells the bulb to resume the last used value instead of a fixed one.
    /// </summary>
    public static byte[] GetPowerOnBehaviourValue(HuePowerOnBehaviour behaviour) => behaviour switch
    {
        HuePowerOnBehaviour.LastColourAndBrightness =>
            [0x01, 0x01, 0x01, 0x02, 0x01, 0xFF, 0x03, 0x02, 0xFF, 0xFF, 0x04, 0x04, 0xFF, 0xFF, 0xFF, 0xFF],
        HuePowerOnBehaviour.LastState =>
            [0x01, 0x01, 0xFF, 0x02, 0x01, 0xFF, 0x03, 0x02, 0xFF, 0xFF, 0x04, 0x04, 0xFF, 0xFF, 0xFF, 0xFF],
        _ =>
            [0x01, 0x01, 0x01, 0x02, 0x01, 0xFE, 0x03, 0x02, 0x6E, 0x01, 0x04, 0x04, 0xFF, 0xFF, 0xFF, 0xFF]
    };

    public static HuePowerOnBehaviour ParsePowerOnBehaviour(ReadOnlySpan<byte> payload)
    {
        byte? power = null;
        byte? brightness = null;
        var index = 0;
        while (index + 2 <= payload.Length)
        {
            var type = payload[index];
            var length = payload[index + 1];
            if (index + 2 + length > payload.Length) break;
            var value = payload.Slice(index + 2, length);
            if (type == 0x01 && length == 1) power = value[0];
            if (type == 0x02 && length == 1) brightness = value[0];
            index += 2 + length;
        }

        if (power == 0xFF) return HuePowerOnBehaviour.LastState;
        if (brightness == 0xFF) return HuePowerOnBehaviour.LastColourAndBrightness;
        return HuePowerOnBehaviour.AlwaysOn;
    }

    /// <summary>
    /// Builds the effect command for the combined state characteristic (932C32BD-0007):
    /// "06 01 effect" plus "08 01 speed". Verified on the target bulb for all effect ids.
    /// </summary>
    public static byte[] GetEffectValue(HueEffect effect, byte speed) =>
        [0x06, 0x01, (byte)effect, 0x08, 0x01, Math.Clamp(speed, (byte)1, (byte)254)];

    public static byte[] GetEffectStopValue() => [0x06, 0x01, (byte)HueEffect.None];

    public static byte[] GetEffectSpeedValue(byte speed) => [0x08, 0x01, Math.Clamp(speed, (byte)1, (byte)254)];

    /// <summary>
    /// Brightness and colour XY concatenated in one write to the combined characteristic, which
    /// halves the BLE traffic while keeping both values in sync.
    /// </summary>
    public static byte[] GetBrightnessAndColorValue(byte brightness, ushort x, ushort y)
    {
        var clamped = Math.Clamp(brightness, BrightnessMinimum, BrightnessMaximum);
        return
        [
            0x02, 0x01, clamped,
            0x04, 0x04, (byte)(x & 0xFF), (byte)(x >> 8), (byte)(y & 0xFF), (byte)(y >> 8)
        ];
    }
}

public enum HueEffect : byte
{
    None = 0x00,
    Candle = 0x01,
    Fireplace = 0x02,
    Prism = 0x03,
    Sparkle = 0x0A,
    Opal = 0x0B,
    Glisten = 0x0C,
    Underwater = 0x0E,
    Cosmos = 0x0F,
    Sunbeam = 0x10,
    Enchant = 0x11
}

public enum HuePowerOnBehaviour
{
    AlwaysOn,
    LastColourAndBrightness,
    LastState
}
