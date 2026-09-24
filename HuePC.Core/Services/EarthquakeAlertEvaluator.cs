using HuePC.Core.Models;

namespace HuePC.Core.Services;

/// <summary>
/// Decides whether a reported earthquake is worth an alert: strong enough, recent enough and
/// close enough to the configured centre point.
/// </summary>
public static class EarthquakeAlertEvaluator
{
    private const double EarthRadiusKm = 6371.0;
    private const double DegreesToRadians = Math.PI / 180.0;

    public static double DistanceKm(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var deltaLatitude = (latitude2 - latitude1) * DegreesToRadians;
        var deltaLongitude = (longitude2 - longitude1) * DegreesToRadians;
        var a = Math.Sin(deltaLatitude / 2) * Math.Sin(deltaLatitude / 2) +
                Math.Cos(latitude1 * DegreesToRadians) * Math.Cos(latitude2 * DegreesToRadians) *
                Math.Sin(deltaLongitude / 2) * Math.Sin(deltaLongitude / 2);
        return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static bool ShouldAlert(
        EarthquakeEvent earthquake,
        double centerLatitude,
        double centerLongitude,
        double radiusKm,
        double minimumMagnitude,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        ArgumentNullException.ThrowIfNull(earthquake);
        if (earthquake.Magnitude < minimumMagnitude)
        {
            return false;
        }

        var age = now - earthquake.Time;
        if (age > maximumAge || age < TimeSpan.FromMinutes(-5))
        {
            return false;
        }

        return DistanceKm(centerLatitude, centerLongitude, earthquake.Latitude, earthquake.Longitude) <= radiusKm;
    }

    public static EarthquakeEvent? StrongestAlert(IEnumerable<EarthquakeEvent> earthquakes, Func<EarthquakeEvent, bool> predicate)
    {
        EarthquakeEvent? strongest = null;
        foreach (var earthquake in earthquakes)
        {
            if (!predicate(earthquake))
            {
                continue;
            }

            if (strongest is null || earthquake.Magnitude > strongest.Magnitude)
            {
                strongest = earthquake;
            }
        }

        return strongest;
    }

    /// <summary>
    /// True when two feed entries most likely describe the same quake (different agencies publish
    /// the same event with their own ids, times and slightly different locations).
    /// </summary>
    public static bool IsSameQuake(EarthquakeEvent first, EarthquakeEvent second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (Math.Abs(first.Magnitude - second.Magnitude) > 1.5)
        {
            return false;
        }

        if ((first.Time - second.Time).Duration() > TimeSpan.FromMinutes(5))
        {
            return false;
        }

        return DistanceKm(first.Latitude, first.Longitude, second.Latitude, second.Longitude) <= 100;
    }
}
