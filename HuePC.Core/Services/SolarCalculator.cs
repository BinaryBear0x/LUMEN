namespace HuePC.Core.Services;

/// <summary>
/// Local sunrise/sunset calculation (NOAA simplified algorithm). Returns null when the sun does
/// not rise or set that day (polar day/night).
/// </summary>
public static class SolarCalculator
{
    public static (DateTimeOffset? Sunrise, DateTimeOffset? Sunset) GetSunTimes(DateTimeOffset date, double latitude, double longitude)
    {
        var dayOfYear = date.DayOfYear;
        var fractionalYear = 2 * Math.PI / 365.0 * (dayOfYear - 1);
        var equationOfTime = 229.18 * (0.000075
            + 0.001868 * Math.Cos(fractionalYear)
            - 0.032077 * Math.Sin(fractionalYear)
            - 0.014615 * Math.Cos(2 * fractionalYear)
            - 0.040849 * Math.Sin(2 * fractionalYear));
        var declination = 0.006918
            - 0.399912 * Math.Cos(fractionalYear)
            + 0.070257 * Math.Sin(fractionalYear)
            - 0.006758 * Math.Cos(2 * fractionalYear)
            + 0.000907 * Math.Sin(2 * fractionalYear)
            - 0.002697 * Math.Cos(3 * fractionalYear)
            + 0.00148 * Math.Sin(3 * fractionalYear);

        var latitudeRadians = latitude * Math.PI / 180.0;
        var cosHourAngle = Math.Cos(90.833 * Math.PI / 180.0) / (Math.Cos(latitudeRadians) * Math.Cos(declination))
            - Math.Tan(latitudeRadians) * Math.Tan(declination);
        if (cosHourAngle is > 1 or < -1)
        {
            return (null, null);
        }

        var hourAngle = Math.Acos(cosHourAngle) * 180.0 / Math.PI;
        var sunriseMinutes = 720 - 4 * (longitude + hourAngle) - equationOfTime;
        var sunsetMinutes = 720 - 4 * (longitude - hourAngle) - equationOfTime;

        var utcDate = date.ToUniversalTime().Date;
        return (ToTime(utcDate, sunriseMinutes), ToTime(utcDate, sunsetMinutes));
    }

    private static DateTimeOffset? ToTime(DateTime utcDate, double minutes)
    {
        if (minutes is < -60 or > 1500)
        {
            return null;
        }

        return new DateTimeOffset(utcDate, TimeSpan.Zero).AddMinutes(minutes).ToLocalTime();
    }
}
