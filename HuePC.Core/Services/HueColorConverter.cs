namespace HuePC.Core.Services;

public readonly record struct HueRgb(byte R, byte G, byte B);

/// <summary>
/// Converts between sRGB and the CIE xy values the Hue BLE color characteristic expects, and
/// produces display colors for the white temperature range. The sRGB to XY matrix is the one
/// published in Philips' Hue developer documentation.
/// </summary>
public static class HueColorConverter
{
    private const ushort MiredWarmest = 455;
    private const ushort MiredCoolest = 154;

    public static (ushort X, ushort Y) ToXy(byte r, byte g, byte b)
    {
        var red = ToLinear(r);
        var green = ToLinear(g);
        var blue = ToLinear(b);

        var xComponent = red * 0.664511 + green * 0.154324 + blue * 0.162028;
        var yComponent = red * 0.283881 + green * 0.668433 + blue * 0.047685;
        var zComponent = red * 0.000088 + green * 0.072310 + blue * 0.986039;

        var total = xComponent + yComponent + zComponent;
        if (total <= 0)
        {
            return (0, 0);
        }

        return (ToUshort(xComponent / total), ToUshort(yComponent / total));
    }

    public static HueRgb FromXy(ushort x, ushort y)
    {
        if (x == 0 && y == 0)
        {
            return new HueRgb(255, 255, 255);
        }

        var xValue = x / 65535.0;
        var yValue = y / 65535.0;
        if (yValue <= 0)
        {
            return new HueRgb(255, 255, 255);
        }

        var z = 1.0 - xValue - yValue;
        var bigX = xValue / yValue;
        var bigY = 1.0;
        var bigZ = z / yValue;

        var red = bigX * 3.2406 - bigY * 1.5372 - bigZ * 0.4986;
        var green = -bigX * 0.9689 + bigY * 1.8758 + bigZ * 0.0415;
        var blue = bigX * 0.0557 - bigY * 0.2040 + bigZ * 1.0570;

        return new HueRgb(FromLinear(red), FromLinear(green), FromLinear(blue));
    }

    public static HueRgb FromMired(ushort mired)
    {
        var clamped = Math.Clamp(mired, MiredCoolest, MiredWarmest);
        var ratio = (clamped - MiredCoolest) / (double)(MiredWarmest - MiredCoolest);
        var red = (byte)Math.Round(222 + (255 - 222) * ratio);
        var green = (byte)Math.Round(235 + (186 - 235) * ratio);
        var blue = (byte)Math.Round(255 + (118 - 255) * ratio);
        return new HueRgb(red, green, blue);
    }

    private static double ToLinear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static byte FromLinear(double value)
    {
        var clamped = Math.Clamp(value, 0, 1);
        var gamma = clamped <= 0.0031308 ? clamped * 12.92 : 1.055 * Math.Pow(clamped, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(gamma, 0, 1) * 255);
    }

    private static ushort ToUshort(double value) =>
        (ushort)Math.Round(Math.Clamp(value, 0, 1) * 65535);
}
