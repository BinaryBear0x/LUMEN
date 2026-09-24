using System.Globalization;
using System.Text.Json;

namespace HuePC.SystemIntegration;

public sealed record WeatherHour(DateTimeOffset Time, double TemperatureCelsius, int WeatherCode);

public sealed record WeatherSnapshot(
    double TemperatureCelsius,
    int WeatherCode,
    DateTimeOffset FetchedAt,
    IReadOnlyList<WeatherHour>? Hours = null);

/// <summary>
/// Minimal Open-Meteo client (no API key) for the current temperature and WMO weather code.
/// </summary>
public sealed class WeatherClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private bool _disposed;

    public async Task<WeatherSnapshot?> GetAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = string.Create(
                CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current=temperature_2m,weather_code&hourly=temperature_2m,weather_code&forecast_days=2&timezone=auto");
            var json = await _http.GetStringAsync(url, cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("current", out var current) ||
                !current.TryGetProperty("temperature_2m", out var temperature) ||
                !current.TryGetProperty("weather_code", out var code))
            {
                return null;
            }

            return new WeatherSnapshot(
                temperature.GetDouble(),
                code.GetInt32(),
                DateTimeOffset.Now,
                ParseHours(document.RootElement));
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<WeatherHour> ParseHours(JsonElement root)
    {
        var hours = new List<WeatherHour>();
        if (!root.TryGetProperty("hourly", out var hourly) ||
            !hourly.TryGetProperty("time", out var times) ||
            !hourly.TryGetProperty("temperature_2m", out var temperatures) ||
            !hourly.TryGetProperty("weather_code", out var codes))
        {
            return hours;
        }

        var now = DateTimeOffset.Now.AddHours(-1);
        var count = Math.Min(times.GetArrayLength(), Math.Min(temperatures.GetArrayLength(), codes.GetArrayLength()));
        for (var index = 0; index < count && hours.Count < 8; index++)
        {
            if (!DateTime.TryParse(times[index].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                continue;
            }

            var moment = new DateTimeOffset(time);
            if (moment < now)
            {
                continue;
            }

            var value = temperatures[index].ValueKind == JsonValueKind.Number ? temperatures[index].GetDouble() : double.NaN;
            var weatherCode = codes[index].ValueKind == JsonValueKind.Number ? codes[index].GetInt32() : 0;
            if (double.IsNaN(value))
            {
                continue;
            }

            hours.Add(new WeatherHour(moment, value, weatherCode));
        }

        return hours;
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
