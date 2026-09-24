namespace HuePC.Core.Models;

/// <summary>One notification rule: when a matching app posts a Windows notification the bulb either blinks
/// in the rule colour or keeps that colour until the notification is dismissed.
/// </summary>
public sealed record NotificationRule(
    string AppName,
    string ColorHex,
    bool StayUntilRead,
    bool Enabled = true);

/// <summary>A user created light profile: a name and two to eight colours.</summary>
public sealed record CustomProfileSetting(string Name, IReadOnlyList<string> ColorHexes);

public sealed record AppSettings(
    bool NotificationBlinkEnabled = false,
    string NotificationAppFilter = "WhatsApp",
    bool NotificationBlinkAllApps = false,
    IReadOnlyList<NotificationRule>? NotificationRules = null,
    bool BusyLightEnabled = false,
    bool BusyLightMicrophone = true,
    bool BusyLightCamera = true,
    bool SystemEventsEnabled = false,
    bool SystemBatteryAlert = true,
    bool SystemChargerNotice = true,
    bool SystemNetworkAlert = true,
    bool ScreenAmbienceEnabled = false,
    bool CircadianEnabled = false,
    bool WeatherEnabled = false,
    double WeatherLatitude = 41.01,
    double WeatherLongitude = 28.98,
    bool EarthquakeAlertEnabled = false,
    double EarthquakeMinimumMagnitude = 4.0,
    double EarthquakeRadiusKm = 600,
    double EarthquakeLatitude = 39.0,
    double EarthquakeLongitude = 35.0,
    bool HasCompletedFirstRun = false,
    IReadOnlyList<string>? FavoriteEffects = null,
    IReadOnlyList<string>? FavoriteProfiles = null,
    IReadOnlyList<CustomProfileSetting>? CustomProfiles = null);
