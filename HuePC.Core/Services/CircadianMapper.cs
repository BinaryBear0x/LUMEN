using HuePC.Core.Models;

namespace HuePC.Core.Services;

/// <summary>
/// Maps the time of day to a white tone and a brightness factor: cooler in the morning, neutral
/// during the day, warm and dim in the evening.
/// </summary>
public static class CircadianMapper
{
    private static readonly (double Hour, double Mired, double Brightness)[] Keyframes =
    [
        (0, 455, 0.18),
        (6, 420, 0.35),
        (8, 280, 0.85),
        (12, 300, 1.0),
        (17, 320, 1.0),
        (20, 390, 0.8),
        (23, 455, 0.4)
    ];

    public static ushort MiredAt(DateTimeOffset time) => (ushort)Math.Round(Interpolate(time, frame => frame.Mired));

    public static double BrightnessFactorAt(DateTimeOffset time) => Math.Clamp(Interpolate(time, frame => frame.Brightness), 0.1, 1);

    private static double Interpolate(DateTimeOffset time, Func<(double Hour, double Mired, double Brightness), double> selector)
    {
        var hour = time.ToLocalTime().TimeOfDay.TotalHours;
        for (var index = 0; index < Keyframes.Length - 1; index++)
        {
            var current = Keyframes[index];
            var next = Keyframes[index + 1];
            if (hour < current.Hour || hour > next.Hour)
            {
                continue;
            }

            var position = (hour - current.Hour) / (next.Hour - current.Hour);
            return selector(current) + (selector(next) - selector(current)) * position;
        }

        // Wrap around midnight (last keyframe -> first keyframe of the next day).
        var last = Keyframes[^1];
        var first = Keyframes[0];
        var span = 24 - last.Hour + first.Hour;
        var wrapped = hour >= last.Hour ? hour - last.Hour : hour + 24 - last.Hour;
        var wrappedPosition = span <= 0 ? 0 : wrapped / span;
        return selector(last) + (selector(first) - selector(last)) * wrappedPosition;
    }
}
