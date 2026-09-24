using HuePC.Core.Models;

namespace HuePC.Core.Services;

public enum MusicLightMode
{
    Calm,
    Beat,
    Synesthesia
}

/// <summary>
/// Maps an analysed frame to a light colour.
/// Calm uses the spectral centroid (the song's "brightness") across the full hue range, Beat
/// uses the bass share for an energetic warm palette, Synesthesia maps the strongest chroma
/// (pitch class) to a hue. The overall level drives brightness, and the dynamics setting shapes
/// how strongly quiet and loud sections differ.
/// </summary>
public static class MusicLightMapper
{
    private const double CentroidLowHz = 120;
    private const double CentroidHighHz = 4000;

    public static HueRgb ToColor(AudioSpectrumFrame frame, MusicLightMode mode, double dynamicsPercent, double centroidPosition = 0.5)
    {
        var hue = GetHue(frame, mode, centroidPosition);
        var saturation = GetSaturation(frame, mode);
        var value = GetValue(frame.Level, dynamicsPercent);
        return ToColor(hue, saturation, value);
    }

    public static byte ToBrightness(AudioSpectrumFrame frame, double dynamicsPercent)
    {
        var value = GetValue(frame.Level, dynamicsPercent);
        return (byte)Math.Clamp(30 + value * 224, 30, 254);
    }

    public static double GetHue(AudioSpectrumFrame frame, MusicLightMode mode, double centroidPosition = 0.5) => mode switch
    {
        MusicLightMode.Synesthesia => HueFromChroma(frame.Chroma),
        MusicLightMode.Beat => HueFromBassShare(frame),
        _ => HueFromCentroidPosition(centroidPosition)
    };

    public static double HueFromCentroidPosition(double position) => 250.0 * Math.Clamp(position, 0, 1);

    public static double GetSaturation(AudioSpectrumFrame frame, MusicLightMode mode) => mode == MusicLightMode.Calm
        ? Math.Clamp(0.7 + 0.3 * (1 - Math.Clamp(frame.Flatness, 0, 1)), 0, 1)
        : 1.0;

    public static double GetValue(double normalizedLevel, double dynamicsPercent)
    {
        var dynamics = Math.Clamp(dynamicsPercent / 100.0, 0, 1);
        var gamma = 0.35 + 1.25 * dynamics;
        return Math.Clamp(Math.Pow(Math.Clamp(normalizedLevel, 0, 1), gamma), 0.05, 1);
    }

    public static double HueFromCentroid(double centroid)
    {
        var clamped = Math.Clamp(centroid, CentroidLowHz, CentroidHighHz);
        var position = Math.Log(clamped / CentroidLowHz) / Math.Log(CentroidHighHz / CentroidLowHz);
        return 250.0 * position;
    }

    public static double HueFromBassShare(AudioSpectrumFrame frame)
    {
        var total = frame.Bass + frame.Mid + frame.Treble + 1e-6;
        var bassShare = Math.Clamp(frame.Bass / total, 0, 1);
        return 240.0 * (1 - bassShare);
    }

    public static double HueFromChroma(IReadOnlyList<double>? chroma)
    {
        if (chroma is null || chroma.Count < 12)
        {
            return 270;
        }

        double x = 0, y = 0;
        for (var index = 0; index < 12; index++)
        {
            var angle = index * 30 * Math.PI / 180;
            x += chroma[index] * Math.Cos(angle);
            y += chroma[index] * Math.Sin(angle);
        }

        if (Math.Abs(x) < 1e-9 && Math.Abs(y) < 1e-9)
        {
            return 270;
        }

        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    public static HueRgb ToColor(double hue, double saturation, double value) =>
        HsvToRgb(hue, Math.Clamp(saturation, 0, 1), Math.Clamp(value, 0, 1));

    private static HueRgb HsvToRgb(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var h = hue / 60;
        var x = chroma * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = (int)(h % 6) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        var m = value - chroma;
        return new HueRgb(
            (byte)Math.Round(Math.Clamp(r + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b + m, 0, 1) * 255));
    }
}
