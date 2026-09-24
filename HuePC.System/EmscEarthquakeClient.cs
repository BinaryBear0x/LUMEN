using System.Globalization;
using System.Text.Json;
using HuePC.Core.Models;

namespace HuePC.SystemIntegration;

/// <summary>
/// Reads the EMSC (seismicportal.eu) FDSN event feed as a second source next to AFAD. EMSC merges
/// data from many regional agencies and is often the fastest to publish an automatic solution for
/// the Eastern Mediterranean, including Turkey. All times are UTC in the feed and are normalised;
/// the event id is prefixed so it cannot clash with AFAD ids.
/// </summary>
public sealed class EmscEarthquakeClient : IDisposable
{
    private const string FeedUrl = "https://www.seismicportal.eu/fdsnws/event/1/query";
    private const double KilometersPerLatitudeDegree = 111.0;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private bool _disposed;

    public async Task<IReadOnlyList<EarthquakeEvent>?> GetRecentAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        double minimumMagnitude,
        double centerLatitude,
        double centerLongitude,
        double radiusKm,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var latitudeDelta = radiusKm / KilometersPerLatitudeDegree;
            var longitudeScale = Math.Max(0.2, Math.Cos(centerLatitude * Math.PI / 180.0));
            var longitudeDelta = radiusKm / (KilometersPerLatitudeDegree * longitudeScale);

            // Keep the whole URL in a single interpolated string: string.Create's provider only
            // applies to the handler argument, so a concatenation would use the current culture
            // and send decimal commas, which the service rejects.
            var url = string.Create(
                CultureInfo.InvariantCulture,
                $"{FeedUrl}?format=json&orderby=time&limit=100&start={from.UtcDateTime:yyyy-MM-ddTHH:mm:ss}&end={to.UtcDateTime:yyyy-MM-ddTHH:mm:ss}&minmag={minimumMagnitude:0.0}&minlat={centerLatitude - latitudeDelta:0.0}&maxlat={centerLatitude + latitudeDelta:0.0}&minlon={centerLongitude - longitudeDelta:0.0}&maxlon={centerLongitude + longitudeDelta:0.0}");
            var json = await _http.GetStringAsync(url, cancellationToken);
            return ParseFeed(json);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<EarthquakeEvent> ParseFeed(string json)
    {
        var results = new List<EarthquakeEvent>();
        if (string.IsNullOrWhiteSpace(json))
        {
            // The service answers with an empty body when nothing matched.
            return results;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var feature in features.EnumerateArray())
        {
            try
            {
                if (!feature.TryGetProperty("properties", out var properties))
                {
                    continue;
                }

                var unid = properties.TryGetProperty("unid", out var unidElement) ? unidElement.GetString() ?? string.Empty : string.Empty;
                if (unid.Length == 0)
                {
                    continue;
                }

                var timeText = properties.TryGetProperty("time", out var timeElement) ? timeElement.GetString() ?? string.Empty : string.Empty;
                if (!DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                {
                    continue;
                }

                double latitude = 0, longitude = 0;
                if (feature.TryGetProperty("geometry", out var geometry) &&
                    geometry.TryGetProperty("coordinates", out var coordinates) &&
                    coordinates.ValueKind == JsonValueKind.Array &&
                    coordinates.GetArrayLength() >= 2)
                {
                    longitude = coordinates[0].GetDouble();
                    latitude = coordinates[1].GetDouble();
                }

                var place = properties.TryGetProperty("place", out var placeElement) ? placeElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(place))
                {
                    place = properties.TryGetProperty("flynn_region", out var regionElement) ? regionElement.GetString() : null;
                }

                if (string.IsNullOrWhiteSpace(place))
                {
                    place = string.Create(CultureInfo.InvariantCulture, $"{latitude:0.00}, {longitude:0.00}");
                }

                results.Add(new EarthquakeEvent(
                    $"emsc-{unid}",
                    time.ToLocalTime(),
                    latitude,
                    longitude,
                    GetDouble(properties, "mag"),
                    GetDouble(properties, "depth"),
                    place,
                    "EMSC"));
            }
            catch (Exception)
            {
                // A malformed feature must not discard the rest of the feed.
            }
        }

        return results;
    }

    private static double GetDouble(JsonElement properties, string name)
    {
        if (!properties.TryGetProperty(name, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetDouble(),
            JsonValueKind.String => double.TryParse(property.GetString(), CultureInfo.InvariantCulture, out var value) ? value : 0,
            _ => 0
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }
}
