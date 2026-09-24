using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;
using HuePC.Core.Services;

namespace HuePC.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDeviceDiscovery _discovery;
    private readonly IBleTransport _transport;
    private readonly IDiagnosticLogger _logger;
    private readonly IDeviceAliasStore _aliasStore;
    private readonly IDeviceMemoryStore _deviceMemoryStore;
    private readonly Dictionary<string, IBleConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _deviceAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastHueSignatureSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastHueSignatureSampleAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _hueSignatureSampleCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _automaticConnectionAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _scanTimer;
    private readonly DispatcherTimer _brightnessTimer;
    private readonly DispatcherTimer _temperatureTimer;
    private readonly DispatcherTimer _colourTimer;
    private RememberedBleDevice? _rememberedDevice;
    private bool _initialized;
    private bool _disposed;
    private bool _isScanning;
    private bool _applyingRemoteState;
    private bool _hasLightState;
    private ushort _colorX;
    private ushort _colorY;
    private ushort _wheelX;
    private ushort _wheelY;

    [ObservableProperty] private AdapterStatus _adapterStatus = new(false, false, false, "Bluetooth durumu denetleniyor…");
    [ObservableProperty] private string _statusMessage = "LUMEN hazırlanıyor…";
    [ObservableProperty] private string _scanStatus = "Hazır";
    [ObservableProperty] private string _selectedConnectionState = "Bağlı değil";
    [ObservableProperty] private string _selectedPowerState = "Bilinmiyor";
    [ObservableProperty] private string _deviceAliasInput = string.Empty;
    [ObservableProperty] private string _currentPage = "Control";
    [ObservableProperty] private DiscoveredDeviceViewModel? _selectedDevice;
    [ObservableProperty] private bool _isLightOn;
    [ObservableProperty] private double _brightnessPercent = 100;
    [ObservableProperty] private double _colorTemperatureMired = 300;
    [ObservableProperty] private bool _isColorMode;
    [ObservableProperty] private Color _bulbColor = Color.FromRgb(0x3A, 0x42, 0x4A);
    [ObservableProperty] private Color _bulbGlowColor = Colors.Transparent;
    [ObservableProperty] private Color _wheelColor = Color.FromRgb(0xFF, 0xD9, 0xA0);
    [ObservableProperty] private string _deviceModel = "—";
    [ObservableProperty] private string _deviceSoftwareVersion = "—";
    [ObservableProperty] private string _deviceManufacturer = "—";
    [ObservableProperty] private string _deviceZigbeeAddress = "—";
    [ObservableProperty] private string _deviceNameInput = string.Empty;
    [ObservableProperty] private bool _isDeviceDetailsLoaded;
    [ObservableProperty] private bool _isBehaviourAlwaysOn = true;
    [ObservableProperty] private bool _isBehaviourLastColour;
    [ObservableProperty] private bool _isBehaviourLastState;

    public MainWindowViewModel(
        IDeviceDiscovery discovery,
        IBleTransport transport,
        IDiagnosticLogger logger,
        IDeviceAliasStore aliasStore,
        IDeviceMemoryStore deviceMemoryStore,
        IScheduleStore scheduleStore,
        IAppSettingsStore settingsStore)
    {
        _discovery = discovery;
        _transport = transport;
        _logger = logger;
        _aliasStore = aliasStore;
        _deviceMemoryStore = deviceMemoryStore;
        _scheduleStore = scheduleStore;
        _settingsStore = settingsStore;
        _discovery.DeviceDiscovered += OnDeviceDiscovered;
        _discovery.ScanError += OnScanError;

        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _scanTimer.Tick += OnScanTimeout;
        _brightnessTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _brightnessTimer.Tick += async (_, _) => { _brightnessTimer.Stop(); await SendBrightnessAsync(); };
        _temperatureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _temperatureTimer.Tick += async (_, _) => { _temperatureTimer.Stop(); await SendColorTemperatureAsync(); };
        _colourTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _colourTimer.Tick += async (_, _) => { _colourTimer.Stop(); await SendWheelColorAsync(); };
        _effectSpeedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _effectSpeedTimer.Tick += async (_, _) => { _effectSpeedTimer.Stop(); await SendEffectSpeedAsync(); };
        MusicMode = MusicModes[0];
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _scheduleTimer.Tick += async (_, _) => await RunDueSchedulesAsync();
        _scheduleTimer.Start();
    }

    public ObservableCollection<DiscoveredDeviceViewModel> Devices { get; } = [];
    public string LogDirectory => _logger.LogDirectory;
    public bool IsScanning => _isScanning;
    public bool HasDiscoveredDevices => Devices.Count > 0;
    public bool HasSelectedDevice => SelectedDevice is not null;
    public bool IsSelectedDeviceConnected => SelectedConnectionState == "Bağlı";
    public bool IsConnecting => SelectedConnectionState == "Bağlanıyor";
    public bool IsRememberedDeviceSelected => SelectedDevice is not null &&
        string.Equals(SelectedDevice.Info.DeviceKey, _rememberedDevice?.Device.DeviceKey, StringComparison.OrdinalIgnoreCase);
    public bool CanForgetRememberedDevice => IsRememberedDeviceSelected && !_disposed;
    public bool IsControlPage => CurrentPage == "Control";
    public bool IsDevicesPage => CurrentPage == "Devices";
    public bool IsEffectsPage => CurrentPage == "Effects";
    public bool IsProfilesPage => CurrentPage == "Profiles";
    public bool IsMusicPage => CurrentPage == "Music";
    public bool IsNotificationsPage => CurrentPage == "Notifications";
    public bool IsEnvironmentPage => CurrentPage == "Environment";
    public bool IsOverviewPage => CurrentPage == "Overview";
    public bool IsDiagnosticsPage => CurrentPage == "Diagnostics";
    public bool HasLightState => _hasLightState;
    public string PageTitle => CurrentPage switch
    {
        "Overview" => "Genel bakış",
        "Devices" => "Ampuller",
        "Schedules" => "Zamanlayıcı",
        "Effects" => "Efektler",
        "Profiles" => "Işık profilleri",
        "Music" => "Müzik modu",
        "Notifications" => "Bildirimler",
        "Environment" => "Ortam",
        "Diagnostics" => "Tanılama",
        _ => "Kontrol"
    };
    public string PageSubtitle => CurrentPage switch
    {
        "Overview" => "Bugünün özeti: alarmlar, Pomodoro, hava ve bağlantı durumu.",
        "Devices" => "Philips Hue ampulünüzü bu bilgisayara bağlayın.",
        "Schedules" => "Belirlediğiniz saatlerde ampulü açın, kapatın veya Pomodoro çalıştırın.",
        "Effects" => "Ampulün içindeki efektler; bilgisayar kapalıyken de çalışır.",
        "Profiles" => "Seçtiğiniz renkler arasında yumuşak geçiş yapan ışık profilleri.",
        "Music" => "Bilgisayarda çalan sesin spektrumuna göre renk ve parlaklık.",
        "Notifications" => "Uygulama bazlı renk kuralları, meşgul ışığı ve sistem olayları.",
        "Environment" => "Ekran ortamı, gün ritmi ve hava durumuna göre otomatik ışık.",
        "Diagnostics" => "Bağlantı sınaması ve çalışma günlükleri.",
        _ => "Bağlı ampulünüzü buradan yönetin."
    };
    public Brush ConnectionDotBrush => IsSelectedDeviceConnected ? Brushes.MediumSeaGreen : Brushes.DarkGray;
    public string LightModeLabel => IsColorMode ? "Renk" : "Beyaz";
    public string ColorTemperatureLabel => $"{Math.Round(1000000 / Math.Max(1, ColorTemperatureMired)):0} K";
    public string BrightnessLabel => $"{Math.Round(BrightnessPercent):0}%";

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            try
            {
                var aliases = await _aliasStore.LoadAsync();
                foreach (var pair in aliases) _deviceAliases[pair.Key] = pair.Value;
            }
            catch (Exception exception)
            {
                await LogAsync("Warning", "Yerel ampul adları yüklenemedi.", ErrorData(exception));
            }

            try
            {
                _rememberedDevice = await _deviceMemoryStore.LoadAsync();
            }
            catch (Exception exception)
            {
                _rememberedDevice = null;
                await LogAsync("Warning", "Hatırlanan ampul ayarı okunamadı.", ErrorData(exception));
            }

            if (_rememberedDevice is not null)
            {
                var remembered = _rememberedDevice.Device;
                _deviceAliases.TryGetValue(remembered.DeviceKey, out var alias);
                SelectedDevice = new DiscoveredDeviceViewModel(remembered, alias);
                Devices.Add(SelectedDevice);
                RaiseDeviceProperties();
            }

            AdapterStatus = await _discovery.GetAdapterStatusAsync();
            await LogAsync("Information", "HuePC açıldı ve Bluetooth durumu denetlendi.", AdapterStatus);
            if (!AdapterStatus.IsAvailable || !AdapterStatus.IsLowEnergySupported)
            {
                StatusMessage = "Bu bilgisayarda Bluetooth LE kullanılamıyor. Bluetooth donanımını denetleyin.";
                ScanStatus = "Bluetooth kullanılamıyor";
                return;
            }

            if (!AdapterStatus.IsBluetoothEnabled)
            {
                StatusMessage = "Bluetooth kapalı. Windows Ayarları'ndan Bluetooth'u açıp yeniden deneyin.";
                ScanStatus = "Bluetooth kapalı";
                return;
            }

            if (_rememberedDevice is not null)
            {
                StatusMessage = "Hatırlanan ampul aranıyor…";
            }

            await LoadSchedulesAsync();
            await LoadNotificationSettingsAsync();
            await LoadEnvironmentSettingsAsync();
            await ApplyLoadedModeSettingsAsync();
            await StartScanAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = "LUMEN başlatılamadı. Uygulamayı yeniden açıp tekrar deneyin.";
            ScanStatus = "Başlatılamadı";
            await LogAsync("Error", "Uygulama başlatılamadı.", ErrorData(exception));
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        if (_disposed || _isScanning) return;
        try
        {
            AdapterStatus = await _discovery.GetAdapterStatusAsync();
            if (!AdapterStatus.IsAvailable || !AdapterStatus.IsLowEnergySupported || !AdapterStatus.IsBluetoothEnabled)
            {
                StatusMessage = !AdapterStatus.IsBluetoothEnabled
                    ? "Bluetooth kapalı. Windows Ayarları'ndan Bluetooth'u açıp yeniden deneyin."
                    : "Bluetooth kullanılamıyor. Bilgisayarınızın Bluetooth ayarlarını denetleyin.";
                ScanStatus = "Arama başlatılamadı";
                await LogAsync("Warning", "Bluetooth taraması için adaptör hazır değildi.", AdapterStatus);
                return;
            }

            await _discovery.StartScanningAsync();
            _isScanning = true;
            var durationSeconds = _rememberedDevice is null ? 30 : 45;
            _scanTimer.Interval = TimeSpan.FromSeconds(durationSeconds);
            ScanStatus = $"Ampuller aranıyor · {durationSeconds} sn";
            StatusMessage = _rememberedDevice is null
                ? "Yakındaki Philips Hue ampulleri aranıyor."
                : "Hatırlanan Philips Hue ampulü aranıyor.";
            OnPropertyChanged(nameof(IsScanning));
            _scanTimer.Stop();
            _scanTimer.Start();
            StartScanCommand.NotifyCanExecuteChanged();
            StopScanCommand.NotifyCanExecuteChanged();
            await LogAsync("Information", "Kullanıcı yakındaki Hue ampullerini aramaya başladı.", new { DurationSeconds = durationSeconds });
        }
        catch (Exception exception)
        {
            _isScanning = false;
            OnPropertyChanged(nameof(IsScanning));
            ScanStatus = "Arama başlatılamadı";
            StatusMessage = "Ampuller aranamadı. Bluetooth ayarlarını kontrol edip yeniden deneyin.";
            await LogAsync("Error", "Bluetooth taraması başlatılamadı.", ErrorData(exception));
        }
    }

    private bool CanStartScan() => !_isScanning && !_disposed;

    [RelayCommand(CanExecute = nameof(CanStopScan))]
    private async Task StopScanAsync() => await StopScanningCoreAsync("Kullanıcı ampul aramasını durdurdu.");

    private bool CanStopScan() => _isScanning && !_disposed;

    private async void OnScanTimeout(object? sender, EventArgs e)
    {
        if (_rememberedDevice is not null && !IsSelectedDeviceConnected)
        {
            await StopScanningCoreAsync("Ampul araması zaman aşımına uğradı.");
            ScanStatus = "Bağlantı bekleniyor";
            StatusMessage = "Ampule ulaşılamıyor. Ampulün açık ve bilgisayarınıza yakın olduğundan emin olun.";
            return;
        }

        await StopScanningCoreAsync("30 saniyelik ampul araması tamamlandı.");
    }

    private async Task StopScanningCoreAsync(string reason)
    {
        if (!_isScanning) return;
        _scanTimer.Stop();
        try
        {
            await _discovery.StopScanningAsync();
            _isScanning = false;
            OnPropertyChanged(nameof(IsScanning));
            if (!IsSelectedDeviceConnected)
                ScanStatus = Devices.Count == 0 ? "Ampul bulunamadı" :
                    SelectedDevice is not null && _connections.ContainsKey(SelectedDevice.Info.DeviceKey)
                        ? "Bağlantı bekleniyor"
                        : "Ampul bulundu";
            StartScanCommand.NotifyCanExecuteChanged();
            StopScanCommand.NotifyCanExecuteChanged();
            await LogAsync("Information", reason);
        }
        catch (Exception exception)
        {
            StatusMessage = "Ampul araması durdurulamadı.";
            await LogAsync("Warning", "Bluetooth taraması durdurulamadı.", ErrorData(exception));
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnectSelected))]
    private async Task ConnectSelectedAsync() => await ConnectSelectedCoreAsync(isAutomatic: false);

    private async Task ConnectSelectedCoreAsync(bool isAutomatic)
    {
        var selected = SelectedDevice;
        if (selected is null || _disposed) return;
        var key = selected.Info.DeviceKey;

        if (_connections.TryGetValue(key, out var existing))
        {
            if (existing.ConnectionState == "Connected")
            {
                SelectedConnectionState = "Bağlı";
                StatusMessage = $"{selected.DisplayName} zaten bağlı.";
                RaiseConnectionProperties();
                return;
            }

            if (isAutomatic) return;
            DetachConnection(existing);
            _connections.Remove(key);
            await existing.DisposeAsync();
        }

        try
        {
            StatusMessage = $"{selected.DisplayName} için bağlantı kuruluyor…";
            SelectedConnectionState = "Bağlanıyor";
            RaiseConnectionProperties();
            await LogAsync("Information", isAutomatic ? "Hatırlanan ampule otomatik bağlantı başlatıldı." : "Kullanıcı ampule bağlanmayı istedi.", selected.Info);

            var connection = await _transport.ConnectAsync(selected.Info);
            _connections[key] = connection;
            AttachConnection(connection);

            try
            {
                await _deviceMemoryStore.SaveAsync(selected.Info);
                _rememberedDevice = new RememberedBleDevice(selected.Info, DateTimeOffset.UtcNow);
                OnPropertyChanged(nameof(IsRememberedDeviceSelected));
                ForgetRememberedDeviceCommand.NotifyCanExecuteChanged();
                await LogAsync("Information", "Ampul sonraki açılışta otomatik bağlantı için kaydedildi.", new
                {
                    Device = selected.Info.Address,
                    selected.Info.AddressType,
                    selected.Info.Name,
                    DeviceKey = selected.Info.DeviceKey
                });
            }
            catch (Exception exception)
            {
                await LogAsync("Error", "Ampul otomatik bağlantı için kaydedilemedi.", ErrorData(exception));
            }

            SelectedConnectionState = connection.ConnectionState == "Connected" ? "Bağlı" : "Bağlanıyor";
            ScanStatus = SelectedConnectionState;
            StatusMessage = connection.ConnectionState == "Connected"
                ? $"{selected.DisplayName} bağlandı."
                : $"{selected.DisplayName} için bağlantı isteği başlatıldı.";
            RaiseConnectionProperties();
            await LogAsync("Information", "Windows Bluetooth oturumu hazırlandı.", new
            {
                Device = selected.Info.Address,
                connection.ConnectionState
            });
            if (_isScanning) await StopScanningCoreAsync("Ampul için bağlantı başlatıldı.");
        }
        catch (Exception exception)
        {
            SelectedConnectionState = "Bağlantı kurulamadı";
            StatusMessage = "Ampule bağlanılamadı. Ampulün açık ve bilgisayarınıza yakın olduğundan emin olun.";
            ScanStatus = "Bağlantı kurulamadı";
            RaiseConnectionProperties();
            await LogAsync("Error", "Ampule Bluetooth bağlantısı kurulamadı.", new { Device = selected.Info.Address, Error = ErrorData(exception) });
        }
        finally
        {
            ConnectSelectedCommand.NotifyCanExecuteChanged();
            DisconnectSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanConnectSelected() => SelectedDevice is not null && !_disposed && HasStableHueSignature(SelectedDevice.Info);

    private static bool HasHueServiceSignature(BleDeviceInfo device) =>
        device.AdvertisedServiceUuids.Contains(HueAdvertisementMatcher.SignifyMemberServiceUuid, StringComparer.OrdinalIgnoreCase);

    private bool HasStableHueSignature(BleDeviceInfo device) =>
        HasHueServiceSignature(device) && _hueSignatureSampleCounts.TryGetValue(device.DeviceKey, out var count) && count >= 3;

    private static BleDeviceInfo MergeAdvertisement(BleDeviceInfo previous, BleDeviceInfo current)
    {
        var serviceUuids = previous.AdvertisedServiceUuids
            .Concat(current.AdvertisedServiceUuids)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var manufacturerData = previous.ManufacturerData
            .Concat(current.ManufacturerData)
            .Distinct()
            .ToArray();
        return current with
        {
            Name = string.IsNullOrWhiteSpace(current.Name) ? previous.Name : current.Name,
            AdvertisedServiceUuids = serviceUuids,
            ManufacturerData = manufacturerData,
            HueMatchReason = serviceUuids.Contains(HueAdvertisementMatcher.SignifyMemberServiceUuid, StringComparer.OrdinalIgnoreCase)
                ? "Hue reklamlarında Signify servis imzası görüldü"
                : current.HueMatchReason ?? previous.HueMatchReason
        };
    }

    [RelayCommand(CanExecute = nameof(CanDisconnectSelected))]
    private async Task DisconnectSelectedAsync()
    {
        if (SelectedDevice is null) return;
        StopMusicMode();
        var key = SelectedDevice.Info.DeviceKey;
        if (_connections.Remove(key, out var connection))
        {
            DetachConnection(connection);
            await connection.DisposeAsync();
        }

        SelectedConnectionState = "Bağlı değil";
        SelectedPowerState = "Bilinmiyor";
        _hasLightState = false;
        _applyingRemoteState = true;
        try { IsLightOn = false; }
        finally { _applyingRemoteState = false; }
        UpdateBulbColors();
        ResetDeviceDetails();
        OnPropertyChanged(nameof(HasLightState));
        StatusMessage = $"{SelectedDevice.DisplayName} bağlantısı kesildi.";
        RaiseConnectionProperties();
        await LogAsync("Information", "Kullanıcı ampul bağlantısını kesti.", new { Device = SelectedDevice.Info.Address });
        DisconnectSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanDisconnectSelected() => SelectedDevice is not null && _connections.ContainsKey(SelectedDevice.Info.DeviceKey) && !_disposed;

    private bool CanControlLight() => SelectedDevice is not null && IsSelectedDeviceConnected && !_disposed;

    partial void OnIsLightOnChanged(bool value)
    {
        UpdateBulbColors();
        if (_applyingRemoteState || !IsSelectedDeviceConnected) return;
        _ = SendPowerAsync(value);
    }

    private async Task SendPowerAsync(bool on)
    {
        var connection = GetSelectedConnection();
        if (connection is null) return;

        StatusMessage = on ? "Ampul açılıyor…" : "Ampul kapatılıyor…";
        var result = await connection.SetPowerAsync(on);
        if (result.IsSuccess)
        {
            SelectedPowerState = on ? "Açık" : "Kapalı";
            StatusMessage = on ? "Ampul açıldı." : "Ampul kapatıldı.";
            UpdateBulbColors();
        }
        else
        {
            _applyingRemoteState = true;
            try { IsLightOn = !on; }
            finally { _applyingRemoteState = false; }
            StatusMessage = $"Komut uygulanamadı: {result.Error ?? result.Status}";
        }

        await LogAsync(result.IsSuccess ? "Information" : "Warning", "Ampul güç komutu işlendi.", new { On = on, result.Status, result.Error });
    }

    [RelayCommand(CanExecute = nameof(CanControlLight))]
    private async Task IdentifyAsync()
    {
        var connection = GetSelectedConnection();
        if (connection is null) return;
        var result = await connection.IdentifyAsync();
        StatusMessage = result.IsSuccess ? "Ampul bir kez yanıp söndü." : $"Komut uygulanamadı: {result.Error ?? result.Status}";
        await LogAsync(result.IsSuccess ? "Information" : "Warning", "Ampul identify komutu işlendi.", new { result.Status, result.Error });
    }

    public void ApplyWheelColor(Color color)
    {
        StopProfile();
        StopMusicMode();
        var (x, y) = HueColorConverter.ToXy(color.R, color.G, color.B);
        _wheelX = x;
        _wheelY = y;
        _applyingRemoteState = true;
        try
        {
            WheelColor = color;
            _colorX = x;
            _colorY = y;
            IsColorMode = true;
            UpdateBulbColors();
        }
        finally
        {
            _applyingRemoteState = false;
        }

        if (!IsSelectedDeviceConnected) return;
        _colourTimer.Stop();
        _colourTimer.Start();
    }

    private async Task SendWheelColorAsync()
    {
        var connection = GetSelectedConnection();
        if (connection is null) return;
        var result = await connection.SetColorAsync(_wheelX, _wheelY);
        if (!result.IsSuccess)
        {
            StatusMessage = $"Komut uygulanamadı: {result.Error ?? result.Status}";
            await LogAsync("Warning", "Ampul renk komutu uygulanamadı.", new { result.Status, result.Error });
        }
    }

    [RelayCommand(CanExecute = nameof(CanControlLight))]
    private async Task SaveDeviceNameAsync()
    {
        var connection = GetSelectedConnection();
        if (connection is null) return;
        var name = DeviceNameInput.Trim();
        if (name.Length == 0)
        {
            StatusMessage = "Cihaz adı boş olamaz.";
            return;
        }

        var result = await connection.SetDeviceNameAsync(name);
        if (result.IsSuccess)
        {
            StatusMessage = "Ampulün cihaz adı güncellendi.";
            var info = await connection.ReadDeviceInfoAsync();
            if (info is not null && SelectedDevice?.Info.DeviceKey == connection.Device.DeviceKey)
            {
                DeviceNameInput = info.DeviceName ?? name;
            }
        }
        else
        {
            StatusMessage = $"Cihaz adı kaydedilemedi: {result.Error ?? result.Status}";
        }

        await LogAsync(result.IsSuccess ? "Information" : "Warning", "Ampul cihaz adı istendi.", new { Name = name, result.Status, result.Error });
    }

    partial void OnIsBehaviourAlwaysOnChanged(bool value)
    {
        if (value) _ = SendPowerOnBehaviourAsync(HuePowerOnBehaviour.AlwaysOn);
    }

    partial void OnIsBehaviourLastColourChanged(bool value)
    {
        if (value) _ = SendPowerOnBehaviourAsync(HuePowerOnBehaviour.LastColourAndBrightness);
    }

    partial void OnIsBehaviourLastStateChanged(bool value)
    {
        if (value) _ = SendPowerOnBehaviourAsync(HuePowerOnBehaviour.LastState);
    }

    private async Task SendPowerOnBehaviourAsync(HuePowerOnBehaviour behaviour)
    {
        if (_applyingRemoteState || !IsSelectedDeviceConnected) return;
        var connection = GetSelectedConnection();
        if (connection is null) return;

        var result = await connection.SetPowerOnBehaviourAsync(behaviour);
        StatusMessage = result.IsSuccess
            ? "Açılış davranışı kaydedildi."
            : $"Açılış davranışı kaydedilemedi: {result.Error ?? result.Status}";
        await LogAsync(result.IsSuccess ? "Information" : "Warning", "Ampul açılış davranışı istendi.", new { Behaviour = behaviour.ToString(), result.Status, result.Error });
    }

    private async Task LoadDeviceDetailsAsync(IBleConnection connection)
    {
        try
        {
            var info = await connection.ReadDeviceInfoAsync();
            if (info is not null && SelectedDevice?.Info.DeviceKey == connection.Device.DeviceKey)
            {
                DeviceModel = string.IsNullOrWhiteSpace(info.Model) ? "—" : info.Model;
                DeviceSoftwareVersion = string.IsNullOrWhiteSpace(info.SoftwareVersion) ? "—" : info.SoftwareVersion;
                DeviceManufacturer = string.IsNullOrWhiteSpace(info.Manufacturer) ? "—" : info.Manufacturer;
                DeviceZigbeeAddress = string.IsNullOrWhiteSpace(info.ZigbeeAddress) ? "—" : info.ZigbeeAddress;
                DeviceNameInput = info.DeviceName ?? string.Empty;
                IsDeviceDetailsLoaded = true;
            }

            var behaviour = await connection.ReadPowerOnBehaviourAsync();
            if (behaviour is not null && SelectedDevice?.Info.DeviceKey == connection.Device.DeviceKey)
            {
                _applyingRemoteState = true;
                try { ApplyBehaviourFlags(behaviour.Value); }
                finally { _applyingRemoteState = false; }
            }
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Ampul ayrıntıları yüklenemedi.", ErrorData(exception));
        }
    }

    private void ApplyBehaviourFlags(HuePowerOnBehaviour behaviour)
    {
        IsBehaviourAlwaysOn = behaviour == HuePowerOnBehaviour.AlwaysOn;
        IsBehaviourLastColour = behaviour == HuePowerOnBehaviour.LastColourAndBrightness;
        IsBehaviourLastState = behaviour == HuePowerOnBehaviour.LastState;
    }

    private void ResetDeviceDetails()
    {
        IsDeviceDetailsLoaded = false;
        DeviceModel = "—";
        DeviceSoftwareVersion = "—";
        DeviceManufacturer = "—";
        DeviceZigbeeAddress = "—";
        DeviceNameInput = string.Empty;
        _applyingRemoteState = true;
        try { ApplyBehaviourFlags(HuePowerOnBehaviour.LastColourAndBrightness); }
        finally { _applyingRemoteState = false; }
    }

    private IBleConnection? GetSelectedConnection()
    {
        if (SelectedDevice is null) return null;
        return _connections.TryGetValue(SelectedDevice.Info.DeviceKey, out var connection) && connection.ConnectionState == "Connected"
            ? connection
            : null;
    }

    private async Task SendBrightnessAsync()
    {
        var connection = GetSelectedConnection();
        if (connection is null) return;
        var value = (byte)Math.Clamp(1 + Math.Round(BrightnessPercent / 100.0 * 253), 1, 254);
        var result = await connection.SetBrightnessAsync(value);
        if (!result.IsSuccess)
        {
            StatusMessage = $"Komut uygulanamadı: {result.Error ?? result.Status}";
            await LogAsync("Warning", "Ampul parlaklık komutu uygulanamadı.", new { value, result.Status, result.Error });
        }
    }

    private async Task SendColorTemperatureAsync()
    {
        var connection = GetSelectedConnection();
        if (connection is null) return;
        var value = (ushort)Math.Clamp(Math.Round(ColorTemperatureMired), HueLightControlProtocol.ColorTemperatureMinimumMired, HueLightControlProtocol.ColorTemperatureMaximumMired);

        StopProfile();

        // Moving the temperature slider always returns the bulb to white mode, even when a colour
        // was active before.
        StopMusicMode();
        IsColorMode = false;
        UpdateBulbColors();

        var result = await connection.SetColorTemperatureAsync(value);
        if (!result.IsSuccess)
        {
            StatusMessage = $"Komut uygulanamadı: {result.Error ?? result.Status}";
            await LogAsync("Warning", "Ampul renk sıcaklığı komutu uygulanamadı.", new { value, result.Status, result.Error });
        }
    }

    partial void OnBrightnessPercentChanged(double value)
    {
        OnPropertyChanged(nameof(BrightnessLabel));
        UpdateBulbColors();
        if (_applyingRemoteState || !IsSelectedDeviceConnected) return;
        _brightnessTimer.Stop();
        _brightnessTimer.Start();
    }

    partial void OnColorTemperatureMiredChanged(double value)
    {
        OnPropertyChanged(nameof(ColorTemperatureLabel));
        UpdateBulbColors();
        if (_applyingRemoteState || !IsSelectedDeviceConnected) return;
        _temperatureTimer.Stop();
        _temperatureTimer.Start();
    }

    [RelayCommand(CanExecute = nameof(CanSaveAlias))]
    private async Task SaveAliasAsync()
    {
        if (SelectedDevice is null) return;
        var key = SelectedDevice.Info.DeviceKey;
        var alias = DeviceAliasInput.Trim();
        if (string.IsNullOrWhiteSpace(alias)) _deviceAliases.Remove(key);
        else _deviceAliases[key] = alias;
        SelectedDevice.Alias = alias;

        try
        {
            await _aliasStore.SaveAsync(_deviceAliases);
            StatusMessage = string.IsNullOrWhiteSpace(alias) ? "Ampul adı sıfırlandı." : $"Ampul adı “{alias}” olarak kaydedildi.";
            await LogAsync("Information", "Kullanıcı ampule yerel bir ad verdi.", new { Device = SelectedDevice.Info.Address, Alias = alias });
        }
        catch (Exception exception)
        {
            StatusMessage = "Ampul adı kaydedilemedi.";
            await LogAsync("Error", "Yerel ampul adı kaydedilemedi.", ErrorData(exception));
        }
    }

    private bool CanSaveAlias() => SelectedDevice is not null && !_disposed;

    [RelayCommand(CanExecute = nameof(CanForgetRememberedDevice))]
    private async Task ForgetRememberedDeviceAsync()
    {
        try
        {
            await _deviceMemoryStore.ForgetAsync();
            _rememberedDevice = null;
            OnPropertyChanged(nameof(IsRememberedDeviceSelected));
            ForgetRememberedDeviceCommand.NotifyCanExecuteChanged();
            StatusMessage = "Otomatik bağlantı kapatıldı. Ampulü yeniden bağlayarak tekrar açabilirsiniz.";
            await LogAsync("Information", "Kullanıcı hatırlanan ampulü kaldırdı.");
        }
        catch (Exception exception)
        {
            StatusMessage = "Otomatik bağlantı tercihi değiştirilemedi.";
            await LogAsync("Error", "Hatırlanan ampul kaldırılamadı.", ErrorData(exception));
        }
    }

    [RelayCommand]
    private void NavigateToPage(string? page)
    {
        CurrentPage = page is "Overview" or "Devices" or "Schedules" or "Effects" or "Profiles" or "Music" or "Notifications" or "Environment" or "Diagnostics" ? page : "Control";
        OnPropertyChanged(nameof(IsControlPage));
        OnPropertyChanged(nameof(IsOverviewPage));
        OnPropertyChanged(nameof(IsDevicesPage));
        OnPropertyChanged(nameof(IsSchedulesPage));
        OnPropertyChanged(nameof(IsEffectsPage));
        OnPropertyChanged(nameof(IsProfilesPage));
        OnPropertyChanged(nameof(IsMusicPage));
        OnPropertyChanged(nameof(IsNotificationsPage));
        OnPropertyChanged(nameof(IsEnvironmentPage));
        OnPropertyChanged(nameof(IsDiagnosticsPage));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
    }

    private void OnDeviceDiscovered(object? sender, DiscoveredDeviceEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var device = e.Device;
            var hasHueSignatureThisAdvertisement = HasHueServiceSignature(device);
            if (hasHueSignatureThisAdvertisement)
            {
                _lastHueSignatureSeen[device.DeviceKey] = device.LastSeenUtc;
                if (!_lastHueSignatureSampleAt.TryGetValue(device.DeviceKey, out var previousSample) ||
                    device.LastSeenUtc - previousSample >= TimeSpan.FromSeconds(3))
                {
                    _hueSignatureSampleCounts[device.DeviceKey] = _hueSignatureSampleCounts.GetValueOrDefault(device.DeviceKey) + 1;
                    _lastHueSignatureSampleAt[device.DeviceKey] = device.LastSeenUtc;
                }
            }

            var found = Devices.FirstOrDefault(item => item.Info.BluetoothAddress == device.BluetoothAddress);
            if (found is null)
            {
                _deviceAliases.TryGetValue(device.DeviceKey, out var alias);
                found = new DiscoveredDeviceViewModel(device, alias);
                Devices.Add(found);
                if (SelectedDevice is null) SelectedDevice = found;
            }
            else
            {
                device = MergeAdvertisement(found.Info, device);
                found.Info = device;
            }

            if (SelectedDevice?.Info.BluetoothAddress == device.BluetoothAddress)
                SelectedDevice.Info = device;

            RaiseDeviceProperties();
            var connectionPending = _connections.TryGetValue(device.DeviceKey, out var currentConnection);
            if (!connectionPending)
            {
                StatusMessage = HasStableHueSignature(device)
                    ? "Philips Hue ampulü hazır. Bağlanmak için ampulü seçin."
                    : "Philips Hue ampulü bulundu. Güvenli bağlantı için doğrulanıyor…";
                ScanStatus = "Ampul bulundu";
            }
            else if (currentConnection?.ConnectionState == "Connected")
            {
                SelectedConnectionState = "Bağlı";
                ScanStatus = "Bağlı";
                RaiseConnectionProperties();
            }

            ConnectSelectedCommand.NotifyCanExecuteChanged();

            if (_rememberedDevice is not null &&
                string.Equals(device.DeviceKey, _rememberedDevice.Device.DeviceKey, StringComparison.OrdinalIgnoreCase) &&
                HasStableHueSignature(device) &&
                hasHueSignatureThisAdvertisement &&
                !_connections.ContainsKey(device.DeviceKey) &&
                _automaticConnectionAttempts.Add(device.DeviceKey))
            {
                SelectedDevice = found;
                StatusMessage = "Hatırlanan ampul bulundu. Otomatik bağlantı hazırlanıyor…";
                _ = ConnectRememberedDeviceAsync(device.DeviceKey);
            }
        }));
    }

    private async Task ConnectRememberedDeviceAsync(string deviceKey)
    {
        const int debounceSeconds = 1;
        try
        {
            await LogAsync("Information", "Hatırlanan ampulden aralıklı üç Hue imzası alındı; kısa doğrulama beklemesi başladı.",
                new { DeviceKey = deviceKey, SignatureSamples = 3, DebounceSeconds = debounceSeconds });
            await Task.Delay(TimeSpan.FromSeconds(debounceSeconds));

            if (!CanRetryRememberedConnection(deviceKey))
            {
                await LogAsync("Information", "Otomatik bağlantı öncesi son Hue reklamı artık güncel değil; deneme atlandı.", new { DeviceKey = deviceKey });
                return;
            }

            StatusMessage = "Hatırlanan ampule otomatik bağlanılıyor…";
            await ConnectSelectedCoreAsync(isAutomatic: true);
            if (_connections.ContainsKey(deviceKey) || SelectedConnectionState != "Bağlantı kurulamadı" || !CanRetryRememberedConnection(deviceKey))
                return;

            await Task.Delay(TimeSpan.FromSeconds(2));
            if (_connections.ContainsKey(deviceKey) || SelectedConnectionState != "Bağlantı kurulamadı" || !CanRetryRememberedConnection(deviceKey))
                return;

            StatusMessage = "Hatırlanan ampul için Windows bağlantısı yeniden deneniyor…";
            await LogAsync("Information", "İlk eşleştirmesiz bağlantı isteği başarısız oldu; güncel Hue imzasıyla bir kez daha deneniyor.",
                new { DeviceKey = deviceKey, Retry = 2, MaxAttempts = 2 });
            await ConnectSelectedCoreAsync(isAutomatic: true);
        }
        finally
        {
            _automaticConnectionAttempts.Remove(deviceKey);
        }
    }

    private bool CanRetryRememberedConnection(string deviceKey) =>
        !_disposed &&
        _isScanning &&
        _rememberedDevice is not null &&
        string.Equals(deviceKey, _rememberedDevice.Device.DeviceKey, StringComparison.OrdinalIgnoreCase) &&
        _lastHueSignatureSeen.TryGetValue(deviceKey, out var lastSeen) &&
        DateTimeOffset.UtcNow - lastSeen <= TimeSpan.FromSeconds(8);

    private void OnScanError(object? sender, string message)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            StatusMessage = "Ampuller aranamadı. Bluetooth ayarlarını kontrol edip yeniden deneyin.";
            ScanStatus = "Arama hatası";
            _ = LogAsync("Error", "Bluetooth taraması sırasında hata oluştu.", new { Detail = message });
        }));
    }

    private void AttachConnection(IBleConnection connection)
    {
        connection.ConnectionStateChanged += OnConnectionStateChanged;
        connection.LightStateChanged += OnLightStateChanged;
    }

    private void DetachConnection(IBleConnection connection)
    {
        connection.ConnectionStateChanged -= OnConnectionStateChanged;
        connection.LightStateChanged -= OnLightStateChanged;
    }

    private void OnLightStateChanged(object? sender, LightStateChangedEventArgs e)
    {
        if (sender is not IBleConnection connection) return;
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SelectedDevice?.Info.DeviceKey != connection.Device.DeviceKey) return;
            ApplyLightState(e.State);
        }));
    }

    private void ApplyLightState(HueLightState state)
    {
        _applyingRemoteState = true;
        try
        {
            if (state.IsOn is not null)
            {
                IsLightOn = state.IsOn.Value;
                SelectedPowerState = state.IsOn.Value ? "Açık" : "Kapalı";
            }

            if (state.Brightness is not null)
            {
                BrightnessPercent = Math.Clamp((state.Brightness.Value - 1) / 253.0 * 100.0, 0, 100);
            }

            if (state.ColorTemperatureMired is not null && state.ColorTemperatureMired != 0xFFFF)
            {
                ColorTemperatureMired = state.ColorTemperatureMired.Value;
                IsColorMode = false;
            }

            if (state.ColorX is not null && state.ColorY is not null && (state.ColorX.Value | state.ColorY.Value) != 0)
            {
                _colorX = state.ColorX.Value;
                _colorY = state.ColorY.Value;
                IsColorMode = true;
            }

            _hasLightState = true;
            ApplyEffectState(state);
            OnPropertyChanged(nameof(HasLightState));
            UpdateBulbColors();
        }
        finally
        {
            _applyingRemoteState = false;
        }
    }

    private void UpdateBulbColors()
    {
        if (!IsLightOn)
        {
            BulbColor = Color.FromRgb(0x3A, 0x42, 0x4A);
            BulbGlowColor = Colors.Transparent;
            return;
        }

        var rgb = IsColorMode
            ? HueColorConverter.FromXy(_colorX, _colorY)
            : HueColorConverter.FromMired((ushort)Math.Clamp(Math.Round(ColorTemperatureMired), HueLightControlProtocol.ColorTemperatureMinimumMired, HueLightControlProtocol.ColorTemperatureMaximumMired));
        if (IsColorMode)
        {
            WheelColor = Color.FromRgb(rgb.R, rgb.G, rgb.B);
        }

        var factor = 0.45 + 0.55 * (Math.Clamp(BrightnessPercent, 0, 100) / 100.0);
        BulbColor = Color.FromRgb(
            (byte)Math.Clamp(rgb.R * factor, 0, 255),
            (byte)Math.Clamp(rgb.G * factor, 0, 255),
            (byte)Math.Clamp(rgb.B * factor, 0, 255));
        BulbGlowColor = Color.FromArgb(150, rgb.R, rgb.G, rgb.B);
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (sender is not IBleConnection connection) return;
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var key = connection.Device.DeviceKey;
            if (SelectedDevice?.Info.DeviceKey == key)
            {
                SelectedConnectionState = ToFriendlyConnectionState(e.State);
                StatusMessage = e.State == "Connected"
                    ? $"{SelectedDevice.DisplayName} bağlandı."
                    : "Ampul bağlantısı bekleniyor…";
                ScanStatus = SelectedConnectionState;
                RaiseConnectionProperties();
            }

            _ = LogAsync("Information", "Ampul bağlantı durumu değişti.", new
            {
                Device = connection.Device.Address,
                State = e.State,
                e.Error
            });
            if (e.State == "Connected" && SelectedDevice?.Info.DeviceKey == key && !_hasLightState)
            {
                _ = RefreshLightStateAsync(connection);
            }

            if (e.State == "Connected" && SelectedDevice?.Info.DeviceKey == key && !IsDeviceDetailsLoaded)
            {
                _ = LoadDeviceDetailsAsync(connection);
            }
            if (e.State == "Connected" && _isScanning)
                _ = StopScanningCoreAsync("Ampul bağlantısı kuruldu; arama durduruldu.");
        }));
    }

    private async Task RefreshLightStateAsync(IBleConnection connection)
    {
        try
        {
            var result = await connection.RefreshLightStateAsync();
            if (!result.IsSuccess)
            {
                await LogAsync("Warning", "Ampul durumu okunamadı.", new { Device = connection.Device.Address, result.Status, result.Error });
            }
        }
        catch (Exception exception)
        {
            await LogAsync("Warning", "Ampul durumu okunamadı.", new { Device = connection.Device.Address, Error = ErrorData(exception) });
        }
    }

    private static string ToFriendlyConnectionState(string state) => state switch
    {
        "Connected" => "Bağlı",
        "Connecting" => "Bağlanıyor",
        "Disconnected" => "Bağlantı bekleniyor",
        _ => "Bağlantı bekleniyor"
    };

    private void UpdateSelectedDeviceState(DiscoveredDeviceViewModel? value)
    {
        DeviceAliasInput = value?.Alias ?? string.Empty;
        StopMusicMode();
        _hasLightState = false;
        SelectedPowerState = "Bilinmiyor";
        _applyingRemoteState = true;
        try { IsLightOn = false; }
        finally { _applyingRemoteState = false; }
        UpdateBulbColors();
        ResetDeviceDetails();
        OnPropertyChanged(nameof(HasLightState));
        if (value is not null && _connections.TryGetValue(value.Info.DeviceKey, out var connection))
        {
            SelectedConnectionState = ToFriendlyConnectionState(connection.ConnectionState);
        }
        else
        {
            SelectedConnectionState = value is null ? "Ampul eklenmedi" : "Bağlı değil";
        }

        RaiseDeviceProperties();
        RaiseConnectionProperties();
        ConnectSelectedCommand.NotifyCanExecuteChanged();
        DisconnectSelectedCommand.NotifyCanExecuteChanged();
        SaveAliasCommand.NotifyCanExecuteChanged();
        ForgetRememberedDeviceCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedDeviceChanged(DiscoveredDeviceViewModel? value) => UpdateSelectedDeviceState(value);

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(IsControlPage));
        OnPropertyChanged(nameof(IsDevicesPage));
        OnPropertyChanged(nameof(IsSchedulesPage));
        OnPropertyChanged(nameof(IsEffectsPage));
        OnPropertyChanged(nameof(IsProfilesPage));
        OnPropertyChanged(nameof(IsMusicPage));
        OnPropertyChanged(nameof(IsNotificationsPage));
        OnPropertyChanged(nameof(IsEnvironmentPage));
        OnPropertyChanged(nameof(IsOverviewPage));
        OnPropertyChanged(nameof(IsDiagnosticsPage));
        if (value == "Diagnostics") RefreshDiagnosticsLog();
    }

    partial void OnIsColorModeChanged(bool value) => OnPropertyChanged(nameof(LightModeLabel));

    private void RaiseDeviceProperties()
    {
        OnPropertyChanged(nameof(HasDiscoveredDevices));
        OnPropertyChanged(nameof(HasSelectedDevice));
        OnPropertyChanged(nameof(IsRememberedDeviceSelected));
    }

    private void RaiseConnectionProperties()
    {
        OnPropertyChanged(nameof(ConnectionDotBrush));
        OnPropertyChanged(nameof(IsSelectedDeviceConnected));
        OnPropertyChanged(nameof(IsConnecting));
        IdentifyCommand.NotifyCanExecuteChanged();
        SaveDeviceNameCommand.NotifyCanExecuteChanged();
        ToggleEffectCommand.NotifyCanExecuteChanged();
        ToggleProfileCommand.NotifyCanExecuteChanged();
        ToggleMusicModeCommand.NotifyCanExecuteChanged();
    }

    private static object ErrorData(Exception exception) => new
    {
        exception.GetType().FullName,
        exception.Message,
        HResult = $"0x{unchecked((uint)exception.HResult):X8}",
        exception.StackTrace
    };

    private async Task LogAsync(string level, string message, object? data = null)
    {
        try { await _logger.WriteAsync(level, message, data); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _scanTimer.Stop();
        _scanTimer.Tick -= OnScanTimeout;
        _brightnessTimer.Stop();
        _temperatureTimer.Stop();
        _colourTimer.Stop();
        _effectSpeedTimer.Stop();
        _scheduleTimer.Stop();
        StopProfile();
        StopMusicMode();
        _spectrumSource.Dispose();
        _notificationWatcher.Dispose();
        DisposeEnvironmentAndModes();
        _discovery.DeviceDiscovered -= OnDeviceDiscovered;
        _discovery.ScanError -= OnScanError;

        try { await _discovery.StopScanningAsync(); }
        catch (Exception exception) { await LogAsync("Warning", "Uygulama kapanırken ampul araması durdurulamadı.", ErrorData(exception)); }

        foreach (var connection in _connections.Values.ToArray())
        {
            DetachConnection(connection);
            try { await connection.DisposeAsync(); }
            catch (Exception exception) { await LogAsync("Warning", "Uygulama kapanırken ampul bağlantısı kapatılamadı.", ErrorData(exception)); }
        }

        _connections.Clear();
        await _discovery.DisposeAsync();
        if (_logger is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
    }
}
