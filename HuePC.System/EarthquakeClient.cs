using System.Globalization;
using System.Text;
using System.Text.Json;
using HuePC.Core.Models;

namespace HuePC.SystemIntegration;

/// <summary>
/// Reads the official AFAD earthquake data. Two endpoints are used: the filter API behind AFAD's
/// own website (fastest, includes preliminary events) and, when that fails, the documented
/// apiv2/event/filter service. Both return Turkish local time as UTC without a zone suffix, so
/// every timestamp is parsed as UTC and normalised to local time.
/// </summary>
public sealed class EarthquakeClient : IDisposable
{
    private const string SiteApiUrl = "https://deprem.afad.gov.tr/EventData/GetEventsByFilter";
    private const string PublicApiUrl = "https://deprem.afad.gov.tr/apiv2/event/filter";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private bool _disposed;

    public async Task<IReadOnlyList<EarthquakeEvent>?> GetRecentAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        double minimumMagnitude,
        CancellationToken cancellationToken = default)
    {
        var fromUtc = from.UtcDateTime;
        var toUtc = to.UtcDateTime;

        var siteEvents = await GetFromSiteApiAsync(fromUtc, toUtc, minimumMagnitude, cancellationToken);
        var publicEvents = await GetFromPublicApiAsync(fromUtc, toUtc, minimumMagnitude, cancellationToken);
        if (siteEvents is null && publicEvents is null)
        {
            return null;
        }

        // The site service is the freshest but returns nothing for long windows; merging both
        // sources keeps the live events from either one without listing a quake twice.
        return (siteEvents ?? [])
            .Concat(publicEvents ?? [])
            .GroupBy(earthquake => earthquake.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private async Task<IReadOnlyList<EarthquakeEvent>?> GetFromSiteApiAsync(
        DateTime fromUtc,
        DateTime toUtc,
        double minimumMagnitude,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                EventSearchFilterList = new[]
                {
                    new { FilterType = 8, Value = fromUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) },
                    new { FilterType = 9, Value = toUtc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) }
                },
                Skip = 0,
                Take = 100,
                SortDescriptor = new { field = "eventDate", dir = "desc" }
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(SiteApiUrl, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseSiteFeed(json, minimumMagnitude);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<EarthquakeEvent>?> GetFromPublicApiAsync(
        DateTime fromUtc,
        DateTime toUtc,
        double minimumMagnitude,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = string.Create(
                CultureInfo.InvariantCulture,
                $"{PublicApiUrl}?start={fromUtc:yyyy-MM-ddTHH:mm:ss}&end={toUtc:yyyy-MM-ddTHH:mm:ss}&minmag={minimumMagnitude:0.0}&limit=100");
            var json = await _http.GetStringAsync(url, cancellationToken);
            return ParsePublicFeed(json);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<EarthquakeEvent> ParseSiteFeed(string json, double minimumMagnitude)
    {
        var results = new List<EarthquakeEvent>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return results;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("eventList", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in events.EnumerateArray())
        {
            try
            {
                var magnitude = GetDouble(item, "magnitude");
                if (magnitude < minimumMagnitude)
                {
                    continue;
                }

                var id = GetIdentifier(item);
                if (id.Length == 0)
                {
                    continue;
                }

                var dateText = item.TryGetProperty("eventDate", out var dateElement) ? dateElement.GetString() ?? string.Empty : string.Empty;
                if (!DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                {
                    continue;
                }

                results.Add(new EarthquakeEvent(
                    id,
                    new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToLocalTime(),
                    GetDouble(item, "latitude"),
                    GetDouble(item, "longitude"),
                    magnitude,
                    GetDouble(item, "depth"),
                    item.TryGetProperty("location", out var location) ? location.GetString() ?? string.Empty : string.Empty));
            }
            catch (Exception)
            {
                // A malformed entry must not discard the rest of the feed.
            }
        }

        return results;
    }

    public static IReadOnlyList<EarthquakeEvent> ParsePublicFeed(string json)
    {
        var results = new List<EarthquakeEvent>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return results;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            try
            {
                var id = item.TryGetProperty("eventID", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
                if (id.Length == 0)
                {
                    continue;
                }

                var dateText = item.TryGetProperty("date", out var dateElement) ? dateElement.GetString() ?? string.Empty : string.Empty;
                if (!DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                {
                    continue;
                }

                results.Add(new EarthquakeEvent(
                    $"afad-{id}",
                    new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToLocalTime(),
                    GetDouble(item, "latitude"),
                    GetDouble(item, "longitude"),
                    GetDouble(item, "magnitude"),
                    GetDouble(item, "depth"),
                    item.TryGetProperty("location", out var location) ? location.GetString() ?? string.Empty : string.Empty));
            }
            catch (Exception)
            {
                // A malformed entry must not discard the rest of the feed.
            }
        }

        return results;
    }

    private static string GetIdentifier(JsonElement item)
    {
        foreach (var name in new[] { "eaeventId", "refId", "id" })
        {
            if (item.TryGetProperty(name, out var element) &&
                element.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            {
                var value = element.ValueKind == JsonValueKind.Number
                    ? element.GetInt64().ToString(CultureInfo.InvariantCulture)
                    : element.GetString() ?? string.Empty;
                if (value.Length > 0 && value != "0")
                {
                    return $"afad-{value}";
                }
            }
        }

        return string.Empty;
    }

    private static double GetDouble(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var property))
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
