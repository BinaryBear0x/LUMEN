namespace HuePC.Core.Models;

/// <summary>
/// One earthquake report from a feed. Times are normalised to Turkish local time (UTC+3).
/// </summary>
public sealed record EarthquakeEvent(
    string Id,
    DateTimeOffset Time,
    double Latitude,
    double Longitude,
    double Magnitude,
    double DepthKm,
    string Location,
    string Source = "AFAD")
{
    public string DisplayText => $"M{Magnitude:0.0} · {Location}";
}
