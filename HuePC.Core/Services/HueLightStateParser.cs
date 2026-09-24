using HuePC.Core.Models;

namespace HuePC.Core.Services;

/// <summary>
/// Parses the Hue combined state characteristic (932C32BD-0007), a TLV list where each entry is
/// "type, length, value": 01/1 power, 02/1 brightness, 03/2 color temperature (little endian),
/// 04/4 color XY (two little endian 16-bit values).
/// </summary>
public static class HueLightStateParser
{
    public static HueLightState ParseCombined(ReadOnlySpan<byte> payload)
    {
        bool? power = null;
        byte? brightness = null;
        ushort? temperature = null;
        ushort? colorX = null;
        ushort? colorY = null;
        byte? effect = null;
        byte? effectSpeed = null;

        var index = 0;
        while (index + 2 <= payload.Length)
        {
            var type = payload[index];
            var length = payload[index + 1];
            if (index + 2 + length > payload.Length)
            {
                break;
            }

            var value = payload.Slice(index + 2, length);
            switch (type)
            {
                case 0x01 when length == 1:
                    power = value[0] != 0;
                    break;
                case 0x02 when length == 1:
                    brightness = value[0];
                    break;
                case 0x03 when length == 2:
                    temperature = (ushort)(value[0] | (value[1] << 8));
                    break;
                case 0x04 when length == 4:
                    colorX = (ushort)(value[0] | (value[1] << 8));
                    colorY = (ushort)(value[2] | (value[3] << 8));
                    break;
                case 0x06 when length == 1:
                    effect = value[0];
                    break;
                case 0x08 when length == 1:
                    effectSpeed = value[0];
                    break;
            }

            index += 2 + length;
        }

        return new HueLightState(power, brightness, temperature, colorX, colorY, effect, effectSpeed);
    }
}
