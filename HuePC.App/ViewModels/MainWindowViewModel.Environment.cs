using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HuePC.Core.Models;
using HuePC.Core.Services;
using HuePC.SystemIntegration;

namespace HuePC.App.ViewModels;

public sealed class WeatherHourViewModel
{
    public WeatherHourViewModel(WeatherSnapshot snapshot, WeatherHour hour)
    {
        TimeText = hour.Time.ToString("HH:mm");
        TemperatureText = $"{hour.TemperatureCelsius:0}°C";
        var mood = WeatherClassifier.Classify(hour.WeatherCode);
        MoodText = MainWindowViewModel.DescribeWeatherMood(mood);
        var color = WeatherClassifier.ColorFor(mood, hour.TemperatureCelsius);
        MoodBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
    }

    public string TimeText { get; }
    public string TemperatureText { get; }
    public string MoodText { get; }
    public System.Windows.Media.Brush MoodBrush { get; }
}

public sealed partial class MainWindowViewModel
{
    private readonly ScreenColorSampler _screenColorSampler = new(4);
    private readonly WeatherClient _weatherClient = new();
    private readonly EarthquakeClient _earthquakeClient = new();
    private readonly EmscEarthquakeClient _emscEarthquakeClient = new();
    private readonly DispatcherTimer _circadianTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _earthquakeTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly HashSet<string> _knownEarthquakeIds = [];
    private readonly List<EarthquakeEvent> _recentlyAlerted = [];
    private DateTimeOffset? _lastWeatherApplyDate;
    private bool _screenWriteBusy;
    private bool _earthquakePrimed;
    private bool _earthquakeFeedFailed;

    [ObservableProperty] private bool _screenAmbienceEnabled;
    [ObservableProperty] private string _screenAmbienceStatusText = "Kapalı";
    [ObservableProperty] private bool _circadianEnabled;
    [ObservableProperty] private string _circadianStatusText = "Kapalı";
    [ObservableProperty] private bool _weatherEnabled;
    [ObservableProperty] private string _weatherStatusText = "Kapalı";
    [ObservableProperty] private double _weatherLatitude = 41.01;
    [ObservableProperty] private double _weatherLongitude = 28.98;
    [ObservableProperty] private bool _earthquakeAlertEnabled;
    [ObservableProperty] private string _earthquakeStatusText = "Kapalı";
    [ObservableProperty] private double _earthquakeMinimumMagnitude = 4.0;
    [ObservableProperty] private double _earthquakeRadiusKm = 600;
    [ObservableProperty] private double _earthquakeLatitude = 39.0;
    [ObservableProperty] private double _earthquakeLongitude = 35.0;
    [ObservableProperty] private string _sunriseSunsetText = "—";

    public ObservableCollection<WeatherHourViewModel> WeatherHours { get; } = [];

    public event EventHandler? MapPickRequested;

    [RelayCommand]
    private void OpenMapPicker() => MapPickRequested?.Invoke(this, EventArgs.Empty);

    public void ApplyMapLocation(double latitude, double longitude)
    {
        WeatherLatitude = Math.Clamp(latitude, -85, 85);
        WeatherLongitude = Math.Clamp(longitude, -180, 180);
        UpdateSunTimes();
        StatusMessage = $"Konum güncellendi: {WeatherLatitude:0.00}, {WeatherLongitude:0.00}";
        _ = RefreshWeatherAsync();
        _ = SaveEnvironmentSettingsAsync();
    }

    private void UpdateSunTimes()
    {
        var (sunrise, sunset) = SolarCalculator.GetSunTimes(DateTimeOffset.Now, WeatherLatitude, WeatherLongitude);
        SunriseSunsetText = sunrise is null || sunset is null
            ? "Bu enlemde bugün güneş doğmuyor ya da batmıyor."
            : $"Gün doğumu {sunrise:HH:mm} · Gün batımı {sunset:HH:mm}";
    }

    public static string DescribeWeatherMood(WeatherMood mood) => mood switch
    {
        WeatherMood.Clear => "açık",
        WeatherMood.Cloudy => "bulutlu",
        WeatherMood.Rain => "yağmurlu",
        WeatherMood.Snow => "karlı",
        WeatherMood.Fog => "puslu",
        _ => "fırtınalı"
    };

    public string WeatherLocationHint => "Enlem / boylam (örn. İstanbul 41,01 / 28,98)";
    public string EarthquakeMagnitudeLabel => $"M {EarthquakeMinimumMagnitude:0.0}";
    public string EarthquakeRadiusLabel => $"{EarthquakeRadiusKm:0} km";
    public string EarthquakeLocationHint => "Merkez enlem / boylam (varsayılan Türkiye merkezi 39,0 / 35,0)";

    private async Task LoadEnvironmentSettingsAsync()
    {
        try
        {
            _circadianTimer.Tick += async (_, _) => await ApplyCircadianNowAsync();
            _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync();
            _earthquakeTimer.Tick += async (_, _) => await CheckEarthquakesAsync();

            var settings = await _settingsStore.LoadAsync();
            _applyingNotificationSettings = true;
            try
            {
                ScreenAmbienceEnabled = settings.ScreenAmbienceEnabled;
                CircadianEnabled = settings.CircadianEnabled;
                WeatherEnabled = settings.WeatherEnabled;
                WeatherLatitude = settings.WeatherLatitude;
                WeatherLongitude = settings.WeatherLongitude;
                EarthquakeAlertEnabled = settings.EarthquakeAlertEnabled;
                EarthquakeMinimumMagnitude = settings.EarthquakeMinimumMagnitude;
                EarthquakeRadiusKm = settings.EarthquakeRadiusKm;
                EarthquakeLatitude = settings.EarthquakeLatitude;
                EarthquakeLongitude = settings.EarthquakeLongitude;
            }
            finally
            {
                _applyingNotificationSettings = false;
            }

            UpdateSunTimes();
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Ortam ayarları yüklenemedi.", ErrorData(exception));
        }
    }

    partial void OnScreenAmbienceEnabledChanged(bool value)
    {
        if (!_applyingNotificationSettings) _ = ApplyScreenAmbienceAsync(value);
    }

    partial void OnCircadianEnabledChanged(bool value)
    {
        if (!_applyingNotificationSettings) ApplyCircadian(value);
    }

    partial void OnWeatherEnabledChanged(bool value)
    {
        if (!_applyingNotificationSettings) _ = ApplyWeatherAsync(value);
    }

    partial void OnWeatherLatitudeChanged(double value)
    {
        UpdateSunTimes();
        _ = SaveEnvironmentSettingsAsync();
    }

    partial void OnWeatherLongitudeChanged(double value)
    {
        UpdateSunTimes();
        _ = SaveEnvironmentSettingsAsync();
    }

    partial void OnEarthquakeAlertEnabledChanged(bool value)
    {
        if (!_applyingNotificationSettings) ApplyEarthquakeAlert(value);
    }

    partial void OnEarthquakeMinimumMagnitudeChanged(double value)
    {
        OnPropertyChanged(nameof(EarthquakeMagnitudeLabel));
        _ = SaveEnvironmentSettingsAsync();
    }

    partial void OnEarthquakeRadiusKmChanged(double value)
    {
        OnPropertyChanged(nameof(EarthquakeRadiusLabel));
        _ = SaveEnvironmentSettingsAsync();
    }

    partial void OnEarthquakeLatitudeChanged(double value) => _ = SaveEnvironmentSettingsAsync();
    partial void OnEarthquakeLongitudeChanged(double value) => _ = SaveEnvironmentSettingsAsync();

    private Task SaveEnvironmentSettingsAsync() =>
        _applyingNotificationSettings ? Task.CompletedTask : SaveAllSettingsAsync();

    private async Task ApplyScreenAmbienceAsync(bool enabled)
    {
        if (_disposed) return;
        if (enabled)
        {
            _screenColorSampler.Sampled -= OnScreenSampled;
            _screenColorSampler.Sampled += OnScreenSampled;
            _screenColorSampler.Start();
            ScreenAmbienceStatusText = "Ekran izleniyor";
            StatusMessage = "Ekran ortamı açık: ampul ekranın ortalama rengini yansıtacak.";
            await LogAsync("Information", "Ekran ortamı açıldı.");
        }
        else
        {
            _screenColorSampler.Sampled -= OnScreenSampled;
            _screenColorSampler.Stop();
            ScreenAmbienceStatusText = "Kapalı";
            await LogAsync("Information", "Ekran ortamı kapatıldı.");
        }

        await SaveEnvironmentSettingsAsync();
    }

    private void OnScreenSampled(object? sender, ScreenSample sample)
    {
        if (_disposed || !ScreenAmbienceEnabled) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_screenWriteBusy)
            {
                return;
            }

            try
            {
                var connection = GetSelectedConnection();
                if (connection is null)
                {
                    ScreenAmbienceStatusText = "Ampul bağlı değil";
                    return;
                }

                if (!ScreenAmbienceEnabled || IsMusicModeRunning || IsProfileRunning || IsPomodoroRunning || _blinkBusy)
                {
                    return;
                }

                _screenWriteBusy = true;
                var (x, y) = HueColorConverter.ToXy(sample.Color.R, sample.Color.G, sample.Color.B);
                var brightness = (byte)Math.Clamp(60 + sample.Luminance * 194, 20, 254);
                await connection.SetColorAndBrightnessAsync(x, y, brightness);
                _applyingRemoteState = true;
                try
                {
                    IsLightOn = true;
                    IsColorMode = true;
                    _colorX = x;
                    _colorY = y;
                    BrightnessPercent = (brightness - 1) / 253.0 * 100.0;
                }
                finally
                {
                    _applyingRemoteState = false;
                }

                UpdateBulbColors();
                ScreenAmbienceStatusText = $"Ekran izleniyor · %{Math.Round(brightness / 254.0 * 100)}";
            }
            catch (Exception exception)
            {
                await LogAsync("Warning", "Ekran ortamı uygulanamadı.", ErrorData(exception));
            }
            finally
            {
                _screenWriteBusy = false;
            }
        }));
    }

    private void ApplyCircadian(bool enabled)
    {
        if (_disposed) return;
        if (enabled)
        {
            _circadianTimer.Start();
            CircadianStatusText = "Gün ritmi etkin";
            StatusMessage = "Gün ritmi açık: ampul gün içinde serinden sıcağa geçecek.";
            _ = ApplyCircadianNowAsync();
            _ = LogAsync("Information", "Gün ritmi açıldı.");
        }
        else
        {
            _circadianTimer.Stop();
            CircadianStatusText = "Kapalı";
            _ = LogAsync("Information", "Gün ritmi kapatıldı.");
        }

        _ = SaveEnvironmentSettingsAsync();
    }

    private async Task ApplyCircadianNowAsync()
    {
        if (_disposed || !CircadianEnabled) return;
        var connection = GetSelectedConnection();
        if (connection is null)
        {
            CircadianStatusText = "Ampul bağlı değil";
            return;
        }

        if (IsMusicModeRunning || IsProfileRunning || IsPomodoroRunning || _blinkBusy)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            var mired = CircadianMapper.MiredAt(now);
            var factor = CircadianMapper.BrightnessFactorAt(now);
            var brightness = (byte)Math.Clamp(1 + factor * 253, 1, 254);
            await connection.SetColorTemperatureAsync(mired);
            await connection.SetBrightnessAsync(brightness);

            _applyingRemoteState = true;
            try
            {
                ColorTemperatureMired = mired;
                IsColorMode = false;
                BrightnessPercent = (brightness - 1) / 253.0 * 100.0;
            }
            finally
            {
                _applyingRemoteState = false;
            }

            UpdateBulbColors();
            CircadianStatusText = $"{Math.Round(1000000.0 / mired):0} K · %{Math.Round(brightness / 254.0 * 100)}";
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Gün ritmi uygulanamadı.", ErrorData(exception));
        }
    }

    private async Task ApplyWeatherAsync(bool enabled)
    {
        if (_disposed) return;
        if (enabled)
        {
            _weatherTimer.Start();
            StatusMessage = "Hava durumu alınıyor…";
            await RefreshWeatherAsync();
            await LogAsync("Information", "Hava durumu izleme açıldı.", new { WeatherLatitude, WeatherLongitude });
        }
        else
        {
            _weatherTimer.Stop();
            WeatherStatusText = "Kapalı";
            await LogAsync("Information", "Hava durumu izleme kapatıldı.");
        }

        await SaveEnvironmentSettingsAsync();
    }

    private async Task RefreshWeatherAsync()
    {
        if (_disposed || !WeatherEnabled) return;
        try
        {
            var snapshot = await _weatherClient.GetAsync(WeatherLatitude, WeatherLongitude);
            if (snapshot is null)
            {
                WeatherStatusText = "Hava durumu alınamadı";
                return;
            }

            var mood = WeatherClassifier.Classify(snapshot.WeatherCode);
            var moodText = DescribeWeatherMood(mood);
            WeatherStatusText = string.Create(
                CultureInfo.CurrentCulture,
                $"{snapshot.TemperatureCelsius:0.#}°C · {moodText} · {snapshot.FetchedAt:HH:mm}");
            WeatherHours.Clear();
            foreach (var hour in snapshot.Hours ?? [])
            {
                WeatherHours.Add(new WeatherHourViewModel(snapshot, hour));
            }

            UpdateSunTimes();
            await LogAsync("Information", "Hava durumu alındı.", new { snapshot.TemperatureCelsius, snapshot.WeatherCode, moodText });

            var now = DateTimeOffset.Now;
            var isMorning = now.Hour is >= 5 and < 11;
            if (!isMorning || _lastWeatherApplyDate?.LocalDateTime.Date == now.LocalDateTime.Date)
            {
                return;
            }

            var connection = GetSelectedConnection();
            if (connection is null || IsMusicModeRunning || IsProfileRunning || IsPomodoroRunning || _blinkBusy)
            {
                return;
            }

            var color = WeatherClassifier.ColorFor(mood, snapshot.TemperatureCelsius);
            var (x, y) = HueColorConverter.ToXy(color.R, color.G, color.B);
            await connection.SetColorAsync(x, y);
            _lastWeatherApplyDate = now;
            StatusMessage = $"Sabah ışığı hava durumuna göre ayarlandı: {moodText}.";
            await LogAsync("Information", "Sabah hava durumu rengi uygulandı.", new { moodText, snapshot.TemperatureCelsius, Color = HexColor.Format(color) });
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Hava durumu alınamadı.", ErrorData(exception));
        }
    }

    [RelayCommand]
    private async Task RefreshWeatherNowAsync()
    {
        if (!WeatherEnabled)
        {
            StatusMessage = "Önce hava durumu izlemesini açın.";
            return;
        }

        await RefreshWeatherAsync();
        StatusMessage = $"Hava durumu: {WeatherStatusText}";
    }

    private void ApplyEarthquakeAlert(bool enabled)
    {
        if (_disposed) return;
        if (enabled)
        {
            _knownEarthquakeIds.Clear();
            _earthquakePrimed = false;
            _earthquakeFeedFailed = false;
            _earthquakeTimer.Start();
            EarthquakeStatusText = "AFAD kontrol ediliyor…";
            StatusMessage = "Deprem alarmı açık: AFAD resmî akışı dakikada bir kontrol edilecek.";
            _ = CheckEarthquakesAsync();
            _ = LogAsync("Information", "Deprem alarmı açıldı.", new { EarthquakeMinimumMagnitude, EarthquakeRadiusKm, EarthquakeLatitude, EarthquakeLongitude });
        }
        else
        {
            _earthquakeTimer.Stop();
            EarthquakeStatusText = "Kapalı";
            _ = LogAsync("Information", "Deprem alarmı kapatıldı.");
        }

        _ = SaveEnvironmentSettingsAsync();
    }

    private async Task CheckEarthquakesAsync()
    {
        if (_disposed || !EarthquakeAlertEnabled) return;
        var now = DateTimeOffset.Now;
        var minimum = Math.Max(1.0, EarthquakeMinimumMagnitude - 1.0);
        var from = now - TimeSpan.FromMinutes(30);
        var to = now + TimeSpan.FromMinutes(5);

        var afadTask = _earthquakeClient.GetRecentAsync(from, to, minimum);
        var emscTask = _emscEarthquakeClient.GetRecentAsync(from, to, minimum, EarthquakeLatitude, EarthquakeLongitude, Math.Max(EarthquakeRadiusKm, 200));
        await Task.WhenAll(afadTask, emscTask);
        var afad = afadTask.Result;
        var emsc = emscTask.Result;

        if (afad is null && emsc is null)
        {
            EarthquakeStatusText = "Kaynaklara ulaşılamadı";
            if (!_earthquakeFeedFailed)
            {
                _earthquakeFeedFailed = true;
                StatusMessage = "Deprem kaynaklarına (AFAD/EMSC) ulaşılamadı; sonraki denemeler sürecek.";
                await LogAsync("Warning", "AFAD ve EMSC deprem servislerine ulaşılamadı.");
            }

            return;
        }

        if (afad is null || emsc is null)
        {
            if (!_earthquakeFeedFailed)
            {
                _earthquakeFeedFailed = true;
                await LogAsync("Warning", "Deprem kaynaklarından biri yanıt vermedi.", new { Afad = afad is not null, Emsc = emsc is not null });
            }
        }
        else
        {
            _earthquakeFeedFailed = false;
        }

        var feed = (afad ?? []).Concat(emsc ?? []).ToArray();
        if (!_earthquakePrimed)
        {
            foreach (var earthquake in feed)
            {
                _knownEarthquakeIds.Add(earthquake.Id);
            }

            _earthquakePrimed = true;
            EarthquakeStatusText = $"İzleniyor (AFAD + EMSC) · son kontrol {now:HH:mm}";
            await LogAsync("Information", "Deprem akışları izlenmeye başlandı.", new { Count = feed.Length, EarthquakeMinimumMagnitude, EarthquakeRadiusKm });
            return;
        }

        var newEvents = feed.Where(earthquake => !_knownEarthquakeIds.Contains(earthquake.Id)).ToArray();
        foreach (var earthquake in newEvents)
        {
            _knownEarthquakeIds.Add(earthquake.Id);
        }

        if (newEvents.Length == 0)
        {
            EarthquakeStatusText = $"İzleniyor (AFAD + EMSC) · son kontrol {now:HH:mm}";
            return;
        }

        _recentlyAlerted.RemoveAll(earthquake => now - earthquake.Time > TimeSpan.FromHours(1));
        var qualified = newEvents
            .Where(earthquake => EarthquakeAlertEvaluator.ShouldAlert(
                earthquake,
                EarthquakeLatitude,
                EarthquakeLongitude,
                EarthquakeRadiusKm,
                EarthquakeMinimumMagnitude,
                now,
                TimeSpan.FromMinutes(30)))
            .Where(earthquake => !_recentlyAlerted.Any(previous => EarthquakeAlertEvaluator.IsSameQuake(previous, earthquake)))
            .ToArray();

        if (qualified.Length == 0)
        {
            EarthquakeStatusText = $"İzleniyor (AFAD + EMSC) · yeni kayıt eşiğin altında · son kontrol {now:HH:mm}";
            return;
        }

        foreach (var earthquake in qualified)
        {
            _recentlyAlerted.Add(earthquake);
        }

        var alert = qualified.MaxBy(earthquake => earthquake.Magnitude)!;
        EarthquakeStatusText = $"Uyarı: {alert.DisplayText} ({alert.Source})";
        StatusMessage = $"Deprem uyarısı: {alert.DisplayText} · {alert.Source}";
        await LogAsync("Warning", "Deprem uyarısı tetiklendi.", new
        {
            alert.Id,
            alert.Source,
            alert.Magnitude,
            alert.Location,
            alert.Latitude,
            alert.Longitude,
            alert.DepthKm,
            Time = alert.Time.ToString("O")
        });
        await FlashEarthquakeAlertAsync(alert);
    }

    private async Task FlashEarthquakeAlertAsync(EarthquakeEvent earthquake)
    {
        if (_blinkBusy || _disposed) return;
        var connection = GetSelectedConnection();
        if (connection is null)
        {
            StatusMessage = "Deprem uyarısı: ampul bağlı değil.";
            return;
        }

        if (IsMusicModeRunning || IsProfileRunning || IsPomodoroRunning || _busyLightActive)
        {
            await LogAsync("Information", "Deprem alarmı atlandı; başka bir mod çalışıyor.", new { earthquake.Id });
            return;
        }

        _blinkBusy = true;
        try
        {
            var saved = CaptureLightState();
            var (x, y) = HueColorConverter.ToXy(0xFF, 0x2B, 0x1A);
            for (var round = 0; round < 3; round++)
            {
                for (var blink = 0; blink < 6; blink++)
                {
                    await connection.SetPowerAsync(true);
                    await connection.SetColorAndBrightnessAsync(x, y, 254);
                    await Task.Delay(200);
                    await connection.SetBrightnessAsync(25);
                    await Task.Delay(200);
                }

                await Task.Delay(600);
            }

            await RestoreLightAsync(connection, saved);
            StatusMessage = $"Deprem uyarısı: {earthquake.DisplayText}";
            await LogAsync("Information", "Deprem alarmı için ampul kırmızı yanıp söndü.", new { earthquake.Id, earthquake.Magnitude });
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Deprem alarmı uygulanamadı.", ErrorData(exception));
        }
        finally
        {
            _blinkBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckEarthquakesNowAsync()
    {
        if (!EarthquakeAlertEnabled)
        {
            StatusMessage = "Önce deprem alarmını açın.";
            return;
        }

        await CheckEarthquakesAsync();
        StatusMessage = $"Deprem kontrolü: {EarthquakeStatusText}";
    }

    [RelayCommand]
    private async Task TestEarthquakeAlertAsync()
    {
        StatusMessage = "Deprem alarmı denemesi: ampul üç kez kırmızı yanıp sönecek.";
        var quake = new EarthquakeEvent("test", DateTimeOffset.Now, 0, 0, 4.5, 10, "Test uyarısı (deneme)");
        await FlashEarthquakeAlertAsync(quake);
    }

    private void DisposeEnvironmentAndModes()
    {
        _screenColorSampler.Sampled -= OnScreenSampled;
        _screenColorSampler.Dispose();
        _circadianTimer.Stop();
        _weatherTimer.Stop();
        _weatherClient.Dispose();
        _earthquakeTimer.Stop();
        _earthquakeClient.Dispose();
        _emscEarthquakeClient.Dispose();
        _deviceUsageMonitor.Dispose();
        _systemEventMonitor.Dispose();
        StopPomodoro();
        CancelWakeUpRamps();
    }
}
