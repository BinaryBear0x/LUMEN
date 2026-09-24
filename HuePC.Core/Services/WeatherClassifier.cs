using HuePC.Core.Models;

namespace HuePC.Core.Services;

public enum WeatherMood
{
    Clear,
    Cloudy,
    Rain,
    Snow,
    Fog,
    Thunder
}

/// <summary>
/// Classifies WMO weather codes from a forecast API and picks a light colour for the mood,
/// taking the temperature into account for clear days.
/// </summary>
public static class WeatherClassifier
{
    public static WeatherMood Classify(int weatherCode) => weatherCode switch
    {
        0 => WeatherMood.Clear,
        1 or 2 or 3 => WeatherMood.Cloudy,
        45 or 48 => WeatherMood.Fog,
        51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => WeatherMood.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherMood.Snow,
        95 or 96 or 99 => WeatherMood.Thunder,
        _ => WeatherMood.Cloudy
    };

    public static HueRgb ColorFor(WeatherMood mood, double temperatureCelsius) => mood switch
    {
        WeatherMood.Clear => temperatureCelsius >= 20 ? new HueRgb(0xFF, 0xC4, 0x6B) : new HueRgb(0xDC, 0xEB, 0xFF),
        WeatherMood.Rain => new HueRgb(0x4A, 0x7B, 0xFF),
        WeatherMood.Snow => new HueRgb(0xBF, 0xE6, 0xFF),
        WeatherMood.Fog => new HueRgb(0xC9, 0xCF, 0xD6),
        WeatherMood.Thunder => new HueRgb(0x8A, 0x2B, 0xE2),
        _ => new HueRgb(0xE8, 0xED, 0xF2)
    };
}
