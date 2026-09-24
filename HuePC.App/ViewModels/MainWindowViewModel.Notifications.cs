using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;
using HuePC.Core.Services;
using HuePC.Notifications;
using HuePC.SystemIntegration;

namespace HuePC.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly HueRgb DefaultNotificationColor = new(0x2E, 0xE6, 0x5B);
    private static readonly HueRgb BusyLightColor = new(0xFF, 0x3B, 0x30);

    private readonly IAppSettingsStore _settingsStore;
    private readonly SystemNotificationWatcher _notificationWatcher = new();
    private readonly DeviceUsageMonitor _deviceUsageMonitor = new();
    private readonly SystemEventMonitor _systemEventMonitor = new();
    private readonly Dictionary<uint, HueRgb> _stickyNotifications = [];
    private SavedLightState? _stickySavedState;
    private bool _blinkBusy;
    private bool _applyingNotificationSettings;
    private bool _busyLightActive;
    private bool _settingsLoaded;
    private bool _seedDefaultRulesOnStartup;

    [ObservableProperty] private bool _notificationBlinkEnabled;
    [ObservableProperty] private string _notificationAppFilter = "WhatsApp";
    [ObservableProperty] private bool _notificationBlinkAllApps;
    [ObservableProperty] private string _notificationStatusText = "Kapalı";
    [ObservableProperty] private string _lastNotificationText = "—";
    [ObservableProperty] private bool _hasNotificationAccess = true;

    [ObservableProperty] private bool _busyLightEnabled;
    [ObservableProperty] private bool _busyLightMicrophone = true;
    [ObservableProperty] private bool _busyLightCamera = true;
    [ObservableProperty] private string _busyLightStatusText = "Kapalı";

    [ObservableProperty] private bool _systemEventsEnabled;
    [ObservableProperty] private bool _systemBatteryAlert = true;
    [ObservableProperty] private bool _systemChargerNotice = true;
    [ObservableProperty] private bool _systemNetworkAlert = true;
    [ObservableProperty] private string _notificationRuleFilter = string.Empty;

    public ObservableCollection<NotificationRuleViewModel> NotificationRules { get; } = [];
    public bool HasNotificationRules => NotificationRules.Count > 0;

    private ICollectionView? _notificationRulesView;

    public ICollectionView NotificationRulesView => _notificationRulesView ??= CreateNotificationRulesView();

    private ICollectionView CreateNotificationRulesView()
    {
        var view = CollectionViewSource.GetDefaultView(NotificationRules);
        view.Filter = item => item is NotificationRuleViewModel rule && MatchesRuleFilter(rule);
        return view;
    }

    private bool MatchesRuleFilter(NotificationRuleViewModel rule) =>
        string.IsNullOrWhiteSpace(NotificationRuleFilter) ||
        rule.AppName.Contains(NotificationRuleFilter.Trim(), StringComparison.OrdinalIgnoreCase) ||
        rule.ColorHex.Contains(NotificationRuleFilter.Trim(), StringComparison.OrdinalIgnoreCase);

    partial void OnNotificationRuleFilterChanged(string value) => NotificationRulesView.Refresh();

    [RelayCommand]
    private async Task EnableAllNotificationRulesAsync()
    {
        foreach (var rule in NotificationRules)
        {
            rule.Enabled = true;
        }

        await SaveAllSettingsAsync();
        StatusMessage = "Tüm bildirim kuralları etkinleştirildi.";
    }

    [RelayCommand]
    private async Task DisableAllNotificationRulesAsync()
    {
        foreach (var rule in NotificationRules)
        {
            rule.Enabled = false;
        }

        await SaveAllSettingsAsync();
        StatusMessage = "Tüm bildirim kuralları kapatıldı.";
    }

    /// <summary>Drag and drop reordering of the notification rules (priority is list order).</summary>
    public void MoveNotificationRule(NotificationRuleViewModel rule, NotificationRuleViewModel target)
    {
        var fromIndex = NotificationRules.IndexOf(rule);
        var toIndex = NotificationRules.IndexOf(target);
        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex)
        {
            return;
        }

        NotificationRules.Move(fromIndex, toIndex);
        _ = SaveAllSettingsAsync();
        StatusMessage = $"“{rule.AppName}” kuralı {toIndex + 1}. sıraya taşındı.";
    }

    public IReadOnlyList<NotificationRulePreset> NotificationRulePresets { get; } =
    [
        new("WhatsApp", "#2EE65B", false),
        new("Outlook", "#0A6CFF", false),
        new("Teams", "#8A2BE2", false),
        new("Discord", "#8A2BE2", false),
        new("Hata", "#FF3B30", true)
    ];

    public string NotificationAccessHint => HasNotificationAccess
        ? "Bildirimler 2 saniyede bir kontrol edilir; HuePC açık ve ampul bağlı olmalıdır. Kurallar uygulama adına göre eşleşir; “okunana kadar kalır” kuralında ampul bildirim okununca önceki durumuna döner."
        : "Windows bildirim erişimi kapalı. Ayarlar → Gizlilik ve güvenlik → Bildirimler bölümünden HuePC'ye izin verin.";

    private async Task LoadNotificationSettingsAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync();
            _applyingNotificationSettings = true;
            try
            {
                NotificationAppFilter = settings.NotificationAppFilter;
                NotificationBlinkAllApps = settings.NotificationBlinkAllApps;
                NotificationBlinkEnabled = settings.NotificationBlinkEnabled;
                BusyLightEnabled = settings.BusyLightEnabled;
                BusyLightMicrophone = settings.BusyLightMicrophone;
                BusyLightCamera = settings.BusyLightCamera;
                SystemEventsEnabled = settings.SystemEventsEnabled;
                SystemBatteryAlert = settings.SystemBatteryAlert;
                SystemChargerNotice = settings.SystemChargerNotice;
                SystemNetworkAlert = settings.SystemNetworkAlert;

                NotificationRules.Clear();
                var rules = settings.NotificationRules ?? DefaultNotificationRules();
                foreach (var rule in rules)
                {
                    AddRuleToCollection(new NotificationRuleViewModel(rule));
                }

                ApplyFavoriteEffects(settings.FavoriteEffects);
                ApplyFavoriteProfiles(settings.FavoriteProfiles);
                ApplyCustomProfiles(settings.CustomProfiles);
                _hasCompletedFirstRun = settings.HasCompletedFirstRun;
                IsFirstRunVisible = !_hasCompletedFirstRun;

                OnPropertyChanged(nameof(HasNotificationRules));
            }
            finally
            {
                _applyingNotificationSettings = false;
            }

            _seedDefaultRulesOnStartup = settings.NotificationRules is null;
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Bildirim ayarları yüklenemedi.", ErrorData(exception));
        }
    }

    /// <summary>
    /// Starts the features that were enabled in the settings file. Runs only after the notification
    /// and environment settings have both been loaded so a startup save cannot wipe the other group.
    /// </summary>
    private async Task ApplyLoadedModeSettingsAsync()
    {
        _settingsLoaded = true;
        if (_seedDefaultRulesOnStartup)
        {
            await SaveAllSettingsAsync();
        }

        if (NotificationBlinkEnabled)
        {
            await ApplyNotificationBlinkAsync(true);
        }

        if (BusyLightEnabled)
        {
            await ApplyBusyLightEnabledAsync(true);
        }

        if (SystemEventsEnabled)
        {
            ApplySystemEventsEnabled(true);
        }

        if (ScreenAmbienceEnabled)
        {
            await ApplyScreenAmbienceAsync(true);
        }

        if (CircadianEnabled)
        {
            ApplyCircadian(true);
        }

        if (WeatherEnabled)
        {
            await ApplyWeatherAsync(true);
        }

        if (EarthquakeAlertEnabled)
        {
            ApplyEarthquakeAlert(true);
        }
    }

    private static IReadOnlyList<NotificationRule> DefaultNotificationRules() =>
    [
        new NotificationRule("WhatsApp", "#2EE65B", false),
        new NotificationRule("Outlook", "#0A6CFF", false),
        new NotificationRule("Teams", "#8A2BE2", false),
        new NotificationRule("Discord", "#8A2BE2", false),
        new NotificationRule("Hata", "#FF3B30", true)
    ];

    private async Task SaveAllSettingsAsync()
    {
        if (_applyingNotificationSettings || !_settingsLoaded)
        {
            return;
        }

        try
        {
            var rules = NotificationRules
                .Where(rule => !string.IsNullOrWhiteSpace(rule.AppName))
                .Select(rule => rule.ToModel())
                .ToArray();
            await _settingsStore.SaveAsync(new AppSettings(
                NotificationBlinkEnabled,
                NotificationAppFilter,
                NotificationBlinkAllApps,
                rules,
                BusyLightEnabled,
                BusyLightMicrophone,
                BusyLightCamera,
                SystemEventsEnabled,
                SystemBatteryAlert,
                SystemChargerNotice,
                SystemNetworkAlert,
                ScreenAmbienceEnabled,
                CircadianEnabled,
                WeatherEnabled,
                WeatherLatitude,
                WeatherLongitude,
                EarthquakeAlertEnabled,
                EarthquakeMinimumMagnitude,
                EarthquakeRadiusKm,
                EarthquakeLatitude,
                EarthquakeLongitude,
                _hasCompletedFirstRun,
                FavoriteEffectNames,
                FavoriteProfileNames,
                CustomProfileSettings));
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Ayarlar kaydedilemedi.", ErrorData(exception));
        }
    }

    private void AddRuleToCollection(NotificationRuleViewModel rule)
    {
        rule.PropertyChanged += OnRulePropertyChanged;
        NotificationRules.Add(rule);
    }

    private void OnRulePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        _ = SaveAllSettingsAsync();

    [RelayCommand]
    private async Task AddNotificationRuleAsync()
    {
        AddRuleToCollection(new NotificationRuleViewModel("Yeni uygulama", "#FFC400", false));
        OnPropertyChanged(nameof(HasNotificationRules));
        await SaveAllSettingsAsync();
        StatusMessage = "Yeni bildirim kuralı eklendi. Uygulama adını ve rengi düzenleyin.";
    }

    [RelayCommand]
    private async Task AddPresetRuleAsync(NotificationRulePreset? preset)
    {
        if (preset is null)
        {
            return;
        }

        AddRuleToCollection(new NotificationRuleViewModel(preset.Name, preset.ColorHex, preset.StayUntilRead));
        OnPropertyChanged(nameof(HasNotificationRules));
        await SaveAllSettingsAsync();
        StatusMessage = $"“{preset.Name}” kuralı eklendi.";
    }

    [RelayCommand]
    private async Task RemoveNotificationRuleAsync(NotificationRuleViewModel? rule)
    {
        if (rule is null)
        {
            return;
        }

        rule.PropertyChanged -= OnRulePropertyChanged;
        NotificationRules.Remove(rule);
        OnPropertyChanged(nameof(HasNotificationRules));
        await SaveAllSettingsAsync();
        StatusMessage = "Bildirim kuralı silindi.";
    }

    [RelayCommand]
    private async Task TestNotificationRuleAsync(NotificationRuleViewModel? rule)
    {
        if (rule is null)
        {
            return;
        }

        var color = HexColor.Parse(rule.ColorHex, DefaultNotificationColor);
        var notification = new NotificationEvent(0, rule.AppName, "Test bildirimi", string.Empty, DateTimeOffset.Now);
        if (rule.StayUntilRead)
        {
            await ApplyStickyNotificationAsync(notification, color);
        }
        else
        {
            await BlinkNotificationAsync(notification, color);
        }
    }

    partial void OnNotificationBlinkEnabledChanged(bool value)
    {
        if (!_applyingNotificationSettings)
        {
            _ = ApplyNotificationBlinkAsync(value);
        }
    }

    partial void OnNotificationAppFilterChanged(string value)
    {
        if (!_applyingNotificationSettings) _ = SaveAllSettingsAsync();
    }

    partial void OnNotificationBlinkAllAppsChanged(bool value)
    {
        if (!_applyingNotificationSettings) _ = SaveAllSettingsAsync();
    }

    partial void OnBusyLightEnabledChanged(bool value)
    {
        if (!_applyingNotificationSettings) _ = ApplyBusyLightEnabledAsync(value);
    }

    partial void OnBusyLightMicrophoneChanged(bool value)
    {
        if (!_applyingNotificationSettings) _ = ReapplyBusyLightAsync();
    }

    partial void OnBusyLightCameraChanged(bool value)
    {
        if (!_applyingNotificationSettings) _ = ReapplyBusyLightAsync();
    }

    partial void OnSystemEventsEnabledChanged(bool value)
    {
        if (!_applyingNotificationSettings)
        {
            ApplySystemEventsEnabled(value);
            _ = SaveAllSettingsAsync();
        }
    }

    partial void OnSystemBatteryAlertChanged(bool value)
    {
        if (!_applyingNotificationSettings) _ = SaveAllSettingsAsync();
    }

    partial void OnSystemChargerNoticeChanged(bool value)
    {
        if (!_applyingNotificationSettings) _ = SaveAllSettingsAsync();
    }

    partial void OnSystemNetworkAlertChanged(bool value)
    {
        if (!_applyingNotificationSettings) _ = SaveAllSettingsAsync();
    }

    partial void OnHasNotificationAccessChanged(bool value) => OnPropertyChanged(nameof(NotificationAccessHint));

    private async Task ApplyNotificationBlinkAsync(bool enabled)
    {
        if (_disposed) return;
        if (enabled)
        {
            var allowed = _notificationWatcher.IsRunning || await SystemNotificationWatcher.EnsureAccessAsync();
            if (!NotificationBlinkEnabled && !_applyingNotificationSettings)
            {
                return;
            }

            HasNotificationAccess = allowed;
            if (!allowed)
            {
                StatusMessage = "Windows bildirim erişimi verilmedi; bildirim yanıp sönmesi açılamadı.";
                _applyingNotificationSettings = true;
                try { NotificationBlinkEnabled = false; }
                finally { _applyingNotificationSettings = false; }
                NotificationStatusText = "İzin yok";
                return;
            }

            _notificationWatcher.NotificationAdded -= OnSystemNotification;
            _notificationWatcher.NotificationAdded += OnSystemNotification;
            _notificationWatcher.NotificationRemoved -= OnSystemNotificationRemoved;
            _notificationWatcher.NotificationRemoved += OnSystemNotificationRemoved;
            _notificationWatcher.Start();
            NotificationStatusText = "Dinliyor";
            StatusMessage = "Bildirimler izleniyor. Kurallara göre ampul yanıp sönecek veya renk tutacak.";
            await LogAsync("Information", "Bildirim izleme açıldı.", new { Rules = NotificationRules.Count });
        }
        else
        {
            _notificationWatcher.NotificationAdded -= OnSystemNotification;
            _notificationWatcher.NotificationRemoved -= OnSystemNotificationRemoved;
            _notificationWatcher.Stop();
            _stickyNotifications.Clear();
            _stickySavedState = null;
            NotificationStatusText = "Kapalı";
            await LogAsync("Information", "Bildirim izleme kapatıldı.");
        }

        if (!_applyingNotificationSettings)
        {
            await SaveAllSettingsAsync();
        }
    }

    private void OnSystemNotification(object? sender, NotificationEvent notification)
    {
        var rule = NotificationRuleMatcher.FindMatch(NotificationRules.Select(item => item.ToModel()), notification.AppName);
        var legacyMatch = rule is null &&
            (NotificationBlinkAllApps ||
             (!string.IsNullOrWhiteSpace(NotificationAppFilter) &&
              notification.AppName.Contains(NotificationAppFilter.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (rule is null && !legacyMatch)
        {
            _ = LogAsync("Information", "Bildirim eşleşmedi.", new { notification.AppName, notification.Title });
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            LastNotificationText = string.IsNullOrWhiteSpace(notification.Title)
                ? notification.AppName
                : $"{notification.AppName}: {notification.Title}";
            if (rule is not null)
            {
                var color = HexColor.Parse(rule.ColorHex, DefaultNotificationColor);
                _ = rule.StayUntilRead
                    ? ApplyStickyNotificationAsync(notification, color)
                    : BlinkNotificationAsync(notification, color);
            }
            else
            {
                _ = BlinkNotificationAsync(notification, DefaultNotificationColor);
            }
        }));
    }

    private void OnSystemNotificationRemoved(object? sender, NotificationEvent notification)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (!_stickyNotifications.Remove(notification.Id))
                {
                    return;
                }

                await LogAsync("Information", "Okunan bildirim için ampul rengi geri alınıyor.", new { notification.Id, notification.AppName });
                if (_stickyNotifications.Count > 0)
                {
                    var remaining = _stickyNotifications.Values.Last();
                    var connection = GetSelectedConnection();
                    if (connection is not null)
                    {
                        var (x, y) = HueColorConverter.ToXy(remaining.R, remaining.G, remaining.B);
                        await connection.SetColorAndBrightnessAsync(x, y, 254);
                    }

                    return;
                }

                if (_busyLightActive)
                {
                    var (x, y) = HueColorConverter.ToXy(BusyLightColor.R, BusyLightColor.G, BusyLightColor.B);
                    var connection = GetSelectedConnection();
                    if (connection is not null)
                    {
                        await connection.SetColorAndBrightnessAsync(x, y, 254);
                    }

                    return;
                }

                await RestoreAfterStickyAsync();
            }
            catch (Exception exception)
            {
                await LogAsync("Warning", "Okunan bildirim sonrası ampul durumu güncellenemedi.", ErrorData(exception));
            }
        }));
    }

    [RelayCommand]
    private async Task TestNotificationBlinkAsync() =>
        await BlinkNotificationAsync(new NotificationEvent(0, "Test", "Test bildirimi", string.Empty, DateTimeOffset.Now), DefaultNotificationColor);

    [RelayCommand]
    private void OpenNotificationSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _ = LogAsync("Warning", "Windows bildirim ayarları açılamadı.", ErrorData(exception));
        }
    }

    private async Task BlinkNotificationAsync(NotificationEvent notification, HueRgb color)
    {
        if (_blinkBusy || _disposed) return;
        var connection = GetSelectedConnection();
        if (connection is null)
        {
            StatusMessage = "Bildirim yanıp sönmesi için ampul bağlı olmalı.";
            return;
        }

        if (IsMusicModeRunning || IsProfileRunning || IsPomodoroRunning || _busyLightActive)
        {
            await LogAsync("Information", "Bildirim yanıp sönmesi atlandı; başka bir mod çalışıyor.", new { notification.AppName });
            return;
        }

        _blinkBusy = true;
        try
        {
            var saved = CaptureLightState();
            var (x, y) = HueColorConverter.ToXy(color.R, color.G, color.B);
            for (var blink = 0; blink < 3; blink++)
            {
                await connection.SetPowerAsync(true);
                await connection.SetColorAndBrightnessAsync(x, y, 254);
                await Task.Delay(220);
                await connection.SetBrightnessAsync(25);
                await Task.Delay(220);
            }

            await RestoreLightAsync(connection, saved);
            StatusMessage = $"Bildirim: {notification.AppName} — ampul yanıp söndü.";
            await LogAsync("Information", "Bildirim için ampul yanıp söndü.", new { notification.AppName, notification.Title, Color = HexColor.Format(color) });
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Bildirim yanıp sönmesi uygulanamadı.", ErrorData(exception));
        }
        finally
        {
            _blinkBusy = false;
        }
    }

    private async Task ApplyStickyNotificationAsync(NotificationEvent notification, HueRgb color)
    {
        if (_disposed) return;
        var connection = GetSelectedConnection();
        if (connection is null)
        {
            StatusMessage = "Bildirim rengi için ampul bağlı olmalı.";
            return;
        }

        if (IsMusicModeRunning || IsProfileRunning || IsPomodoroRunning || _busyLightActive)
        {
            await LogAsync("Information", "Bildirim rengi atlandı; başka bir mod çalışıyor.", new { notification.AppName });
            return;
        }

        try
        {
            if (_stickyNotifications.Count == 0)
            {
                _stickySavedState = CaptureLightState();
            }

            _stickyNotifications[notification.Id == 0 ? (uint)DateTimeOffset.Now.Ticks : notification.Id] = color;
            var (x, y) = HueColorConverter.ToXy(color.R, color.G, color.B);
            var power = await connection.SetPowerAsync(true);
            var colour = await connection.SetColorAndBrightnessAsync(x, y, 254);
            if (!power.IsSuccess || !colour.IsSuccess)
            {
                _stickyNotifications.Clear();
                _stickySavedState = null;
                StatusMessage = "Bildirim rengi uygulanamadı.";
                await LogAsync("Warning", "Bildirim rengi yazılamadı.", new { Power = power.Status, PowerError = power.Error, Colour = colour.Status, ColourError = colour.Error });
                return;
            }

            StatusMessage = $"“{notification.AppName}” bildirimi okunana kadar ampul {HexColor.Format(color)} renginde kalacak.";
            await LogAsync("Information", "Bildirim rengi okunana kadar tutuluyor.", new { notification.AppName, Color = HexColor.Format(color) });
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Bildirim rengi uygulanamadı.", ErrorData(exception));
        }
    }

    private async Task RestoreAfterStickyAsync()
    {
        var saved = _stickySavedState;
        _stickySavedState = null;
        if (saved is null || _disposed)
        {
            return;
        }

        var connection = GetSelectedConnection();
        if (connection is null)
        {
            return;
        }

        try
        {
            await RestoreLightAsync(connection, saved);
            StatusMessage = "Bildirim okundu; ampul önceki durumuna döndü.";
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Ampul önceki durumuna döndürülemedi.", ErrorData(exception));
        }
    }

    private async Task ApplyBusyLightEnabledAsync(bool enabled)
    {
        if (_disposed) return;
        if (enabled)
        {
            _deviceUsageMonitor.UsageChanged -= OnDeviceUsageChanged;
            _deviceUsageMonitor.UsageChanged += OnDeviceUsageChanged;
            _deviceUsageMonitor.Start();
            BusyLightStatusText = "İzleniyor";
            await ReapplyBusyLightAsync();
            await LogAsync("Information", "Meşgul ışığı açıldı.", new { BusyLightMicrophone, BusyLightCamera });
        }
        else
        {
            _deviceUsageMonitor.UsageChanged -= OnDeviceUsageChanged;
            _deviceUsageMonitor.Stop();
            if (_busyLightActive)
            {
                _busyLightActive = false;
                await RestoreAfterBusyLightAsync();
            }

            BusyLightStatusText = "Kapalı";
            await LogAsync("Information", "Meşgul ışığı kapatıldı.");
        }

        if (!_applyingNotificationSettings)
        {
            await SaveAllSettingsAsync();
        }
    }

    private async Task ReapplyBusyLightAsync()
    {
        if (!BusyLightEnabled || _disposed)
        {
            return;
        }

        var inUse = (_deviceUsageMonitor.MicrophoneInUse && BusyLightMicrophone) ||
                     (_deviceUsageMonitor.CameraInUse && BusyLightCamera);
        if (inUse && !_busyLightActive)
        {
            await TurnBusyLightOnAsync();
        }
        else if (!inUse && _busyLightActive)
        {
            _busyLightActive = false;
            await RestoreAfterBusyLightAsync();
        }

        BusyLightStatusText = inUse ? "Mikrofon/kamera kullanımda" : "Beklemede";
    }

    private void OnDeviceUsageChanged(object? sender, (bool Microphone, bool Camera) usage)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                var inUse = (usage.Microphone && BusyLightMicrophone) || (usage.Camera && BusyLightCamera);
                BusyLightStatusText = inUse ? "Mikrofon/kamera kullanımda" : "Beklemede";
                if (!BusyLightEnabled)
                {
                    return;
                }

                if (inUse && !_busyLightActive)
                {
                    await TurnBusyLightOnAsync();
                }
                else if (!inUse && _busyLightActive)
                {
                    _busyLightActive = false;
                    await RestoreAfterBusyLightAsync();
                }
            }
            catch (Exception exception)
            {
                await LogAsync("Warning", "Meşgul ışığı güncellenemedi.", ErrorData(exception));
            }
        }));
    }

    private async Task TurnBusyLightOnAsync()
    {
        var connection = GetSelectedConnection();
        if (connection is null)
        {
            BusyLightStatusText = "Ampul bağlı değil";
            return;
        }

        if (IsMusicModeRunning || IsProfileRunning || IsPomodoroRunning || _blinkBusy)
        {
            await LogAsync("Information", "Meşgul ışığı atlandı; başka bir mod çalışıyor.");
            return;
        }

        try
        {
            _busyLightActive = true;
            _stickySavedState ??= CaptureLightState();
            var (x, y) = HueColorConverter.ToXy(BusyLightColor.R, BusyLightColor.G, BusyLightColor.B);
            var power = await connection.SetPowerAsync(true);
            var colour = await connection.SetColorAndBrightnessAsync(x, y, 254);
            if (!power.IsSuccess || !colour.IsSuccess)
            {
                _busyLightActive = false;
                StatusMessage = "Meşgul ışığı uygulanamadı.";
                await LogAsync("Warning", "Meşgul ışığı yazılamadı.", new { Power = power.Status, PowerError = power.Error, Colour = colour.Status, ColourError = colour.Error });
                return;
            }

            StatusMessage = "Mikrofon/kamera kullanımda; ampul kırmızı.";
            await LogAsync("Information", "Meşgul ışığı kırmızıya alındı.", new { BusyLightStatusText });
        }
        catch (Exception exception)
        {
            _busyLightActive = false;
            await LogAsync("Warning", "Meşgul ışığı uygulanamadı.", ErrorData(exception));
        }
    }

    private async Task RestoreAfterBusyLightAsync()
    {
        if (_stickyNotifications.Count > 0)
        {
            var color = _stickyNotifications.Values.Last();
            var stickyConnection = GetSelectedConnection();
            if (stickyConnection is not null)
            {
                var (x, y) = HueColorConverter.ToXy(color.R, color.G, color.B);
                await stickyConnection.SetColorAndBrightnessAsync(x, y, 254);
            }

            return;
        }

        await RestoreAfterStickyAsync();
    }

    private void ApplySystemEventsEnabled(bool enabled)
    {
        if (enabled)
        {
            _systemEventMonitor.BatteryLow -= OnBatteryLow;
            _systemEventMonitor.BatteryLow += OnBatteryLow;
            _systemEventMonitor.ChargerChanged -= OnChargerChanged;
            _systemEventMonitor.ChargerChanged += OnChargerChanged;
            _systemEventMonitor.NetworkChanged -= OnNetworkChanged;
            _systemEventMonitor.NetworkChanged += OnNetworkChanged;
            _systemEventMonitor.Start();
            StatusMessage = "Sistem bildirimleri izleniyor: pil, şarj ve ağ.";
            _ = LogAsync("Information", "Sistem olayları izleme açıldı.");
        }
        else
        {
            _systemEventMonitor.BatteryLow -= OnBatteryLow;
            _systemEventMonitor.ChargerChanged -= OnChargerChanged;
            _systemEventMonitor.NetworkChanged -= OnNetworkChanged;
            _systemEventMonitor.Stop();
            _ = LogAsync("Information", "Sistem olayları izleme kapatıldı.");
        }
    }

    private void OnBatteryLow(object? sender, int percent)
    {
        if (!SystemEventsEnabled || !SystemBatteryAlert) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            _ = PulseAsync(new HueRgb(0xFF, 0x3B, 0x30), 3, $"Pil azaldı (%{percent}); ampul kırmızı yanıp söndü.")));
    }

    private void OnChargerChanged(object? sender, bool pluggedIn)
    {
        if (!SystemEventsEnabled || !SystemChargerNotice || !pluggedIn) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            _ = PulseAsync(new HueRgb(0x2E, 0xE6, 0x5B), 1, "Şarj takıldı; ampul yeşil yanıp söndü.")));
    }

    private void OnNetworkChanged(object? sender, bool available)
    {
        if (!SystemEventsEnabled || !SystemNetworkAlert || available) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            _ = PulseAsync(new HueRgb(0xFF, 0xC4, 0x00), 3, "Ağ bağlantısı kesildi; ampul sarı yanıp söndü.")));
    }

    private async Task PulseAsync(HueRgb color, int blinks, string message)
    {
        if (_blinkBusy || _disposed) return;
        var connection = GetSelectedConnection();
        if (connection is null)
        {
            StatusMessage = "Sistem bildirimi için ampul bağlı olmalı.";
            return;
        }

        if (IsMusicModeRunning || IsProfileRunning || IsPomodoroRunning || _busyLightActive)
        {
            await LogAsync("Information", "Sistem bildirimi atlandı; başka bir mod çalışıyor.", new { Color = HexColor.Format(color) });
            return;
        }

        _blinkBusy = true;
        try
        {
            var saved = CaptureLightState();
            var (x, y) = HueColorConverter.ToXy(color.R, color.G, color.B);
            for (var blink = 0; blink < Math.Max(1, blinks); blink++)
            {
                await connection.SetPowerAsync(true);
                await connection.SetColorAndBrightnessAsync(x, y, 254);
                await Task.Delay(240);
                await connection.SetBrightnessAsync(30);
                await Task.Delay(240);
            }

            await RestoreLightAsync(connection, saved);
            StatusMessage = message;
            await LogAsync("Information", "Sistem bildirimi için ampul yanıp söndü.", new { Color = HexColor.Format(color), blinks });
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Sistem bildirimi uygulanamadı.", ErrorData(exception));
        }
        finally
        {
            _blinkBusy = false;
        }
    }

    private SavedLightState CaptureLightState() => new(
        IsLightOn,
        IsColorMode,
        _colorX,
        _colorY,
        (ushort)Math.Clamp(Math.Round(ColorTemperatureMired), HueLightControlProtocol.ColorTemperatureMinimumMired, HueLightControlProtocol.ColorTemperatureMaximumMired),
        (byte)Math.Clamp(1 + Math.Round(BrightnessPercent / 100.0 * 253), 1, 254));

    private static async Task RestoreLightAsync(IBleConnection connection, SavedLightState state)
    {
        if (!state.WasOn)
        {
            await connection.SetPowerAsync(false);
        }
        else if (state.WasColorMode)
        {
            await connection.SetColorAndBrightnessAsync(state.ColorX, state.ColorY, state.Brightness);
        }
        else
        {
            await connection.SetColorTemperatureAsync(state.TemperatureMired);
            await connection.SetBrightnessAsync(state.Brightness);
        }
    }

    private sealed record SavedLightState(bool WasOn, bool WasColorMode, ushort ColorX, ushort ColorY, ushort TemperatureMired, byte Brightness);
}

public sealed record NotificationRulePreset(string Name, string ColorHex, bool StayUntilRead);
