using System.Diagnostics;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;
using HuePC.Core.Services;

namespace HuePC.Bluetooth.Gatt;

internal sealed class WindowsBleConnection : IBleConnection
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private readonly BluetoothLEDevice _device;
    private readonly GattSession _session;
    private readonly IDiagnosticLogger _logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Dictionary<GattAttributeKey, GattCharacteristic> _characteristics = [];
    private readonly Dictionary<GattAttributeKey, bool> _indicationModes = [];
    private readonly Dictionary<string, GattCharacteristic> _hueCharacteristics = new(StringComparer.OrdinalIgnoreCase);
    private GattCharacteristic? _lightStateCharacteristic;
    private bool _lightStateSubscribed;
    private bool _disposed;
    private bool _unusable;

    public WindowsBleConnection(BleDeviceInfo device, BluetoothLEDevice bluetoothDevice, GattSession session, IDiagnosticLogger logger)
    {
        Device = device;
        _device = bluetoothDevice;
        _session = session;
        _logger = logger;
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        _session.SessionStatusChanged += OnSessionStatusChanged;
    }

    public BleDeviceInfo Device { get; }
    public string ConnectionState => _device.ConnectionStatus.ToString();
    public bool IsPaired => _device.DeviceInformation.Pairing.IsPaired;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<GattNotificationEventArgs>? NotificationReceived;
    public event EventHandler<LightStateChangedEventArgs>? LightStateChanged;

    public async Task<GattDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var errors = new List<string>();
            var totalTimer = Stopwatch.StartNew();
            var servicesTimer = Stopwatch.StartNew();
            var servicesResult = await WithTimeout(
                _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken),
                cancellationToken);
            servicesTimer.Stop();

            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                var error = $"Servis keşfi başarısız: {servicesResult.Status}.";
                errors.Add(error);
                await LogSafeAsync("Error", error, new { Device = Device.Address, DurationMilliseconds = servicesTimer.ElapsedMilliseconds });
                return new GattDiscoverySnapshot(Device, DateTimeOffset.UtcNow, ConnectionState, [], errors);
            }

            await LogSafeAsync("Information", "GATT servisleri keşfedildi.", new
            {
                Device = Device.Address,
                Count = servicesResult.Services.Count,
                DurationMilliseconds = servicesTimer.ElapsedMilliseconds,
                Services = servicesResult.Services.Select(service => new { Uuid = service.Uuid.ToString("D"), service.AttributeHandle })
            });

            var services = new List<GattServiceSnapshot>();
            foreach (var service in servicesResult.Services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                services.Add(await DiscoverServiceAsync(service, errors, cancellationToken));
            }

            totalTimer.Stop();
            await LogSafeAsync("Information", "GATT keşfi tamamlandı.", new
            {
                Device = Device.Address,
                ServiceCount = services.Count,
                ErrorCount = errors.Count,
                DurationMilliseconds = totalTimer.ElapsedMilliseconds,
                ConnectionState
            });
            return new GattDiscoverySnapshot(Device, DateTimeOffset.UtcNow, ConnectionState, services, errors);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<GattServiceSnapshot> DiscoverServiceAsync(
        GattDeviceService service,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var serviceErrors = new List<string>();
        var includedServiceUuids = new List<string>();
        var characteristics = new List<GattCharacteristicSnapshot>();
        var status = "Success";

        try
        {
            var included = await WithTimeout(
                service.GetIncludedServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken),
                cancellationToken);
            if (included.Status == GattCommunicationStatus.Success)
            {
                includedServiceUuids.AddRange(included.Services.Select(item => item.Uuid.ToString("D").ToUpperInvariant()));
            }
            else
            {
                serviceErrors.Add($"Included services: {included.Status}.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TimeoutException)
        {
            serviceErrors.Add($"Included service keşfi: {exception.Message}");
        }

        try
        {
            var result = await WithTimeout(
                service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken),
                cancellationToken);
            if (result.Status != GattCommunicationStatus.Success)
            {
                status = result.Status.ToString();
                serviceErrors.Add($"Characteristic keşfi: {result.Status}.");
            }
            else
            {
                foreach (var characteristic in result.Characteristics)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    characteristics.Add(await DiscoverCharacteristicAsync(service, characteristic, serviceErrors, cancellationToken));
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TimeoutException)
        {
            if (status == "Success") status = "Error";
            serviceErrors.Add(exception.Message);
        }

        timer.Stop();
        errors.AddRange(serviceErrors.Select(error => $"{service.Uuid}: {error}"));
        await LogSafeAsync(serviceErrors.Count == 0 ? "Information" : "Warning", "GATT servisi incelendi.", new
        {
            Uuid = service.Uuid.ToString("D").ToUpperInvariant(),
            AttributeHandle = service.AttributeHandle,
            ServiceType = "Windows API türü açıklamıyor",
            DiscoveryStatus = status,
            IncludedServiceUuids = includedServiceUuids,
            CharacteristicCount = characteristics.Count,
            DurationMilliseconds = timer.ElapsedMilliseconds,
            Errors = serviceErrors
        });
        return new GattServiceSnapshot(
            service.Uuid.ToString("D").ToUpperInvariant(),
            WindowsGattNames.Service(service.Uuid.ToString("D")),
            "Windows API türü açıklamıyor",
            service.AttributeHandle,
            status,
            includedServiceUuids,
            serviceErrors.Count == 0 ? null : string.Join(" ", serviceErrors),
            timer.ElapsedMilliseconds,
            characteristics);
    }

    private async Task<GattCharacteristicSnapshot> DiscoverCharacteristicAsync(
        GattDeviceService service,
        GattCharacteristic characteristic,
        List<string> serviceErrors,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var key = new GattAttributeKey(
            service.Uuid.ToString("D").ToUpperInvariant(),
            service.AttributeHandle,
            characteristic.Uuid.ToString("D").ToUpperInvariant(),
            characteristic.AttributeHandle);
        _characteristics[key] = characteristic;

        string readStatus = "NotAttempted";
        string? valueHex = null;
        int? valueLength = null;
        string? error = null;

        if (characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
        {
            try
            {
                var read = await WithTimeout(characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken), cancellationToken);
                readStatus = read.Status.ToString();
                if (read.Status == GattCommunicationStatus.Success)
                {
                    valueHex = ReadBuffer(read.Value);
                    valueLength = checked((int)read.Value.Length);
                }
                else
                {
                    error = $"Okuma: {read.Status}.";
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not TimeoutException)
            {
                readStatus = "Error";
                error = exception.Message;
            }
        }

        var descriptors = new List<GattDescriptorSnapshot>();
        try
        {
            var result = await WithTimeout(characteristic.GetDescriptorsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken), cancellationToken);
            if (result.Status == GattCommunicationStatus.Success)
            {
                foreach (var descriptor in result.Descriptors)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    descriptors.Add(await ReadDescriptorAsync(descriptor, cancellationToken));
                }
            }
            else
            {
                error = Append(error, $"Descriptor keşfi: {result.Status}.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TimeoutException)
        {
            error = Append(error, $"Descriptor keşfi: {exception.Message}");
        }

        timer.Stop();
        await LogSafeAsync(error is null ? "Information" : "Warning", "GATT characteristic incelendi.", new
        {
            Key = key,
            Operations = characteristic.CharacteristicProperties.ToString(),
            ProtectionLevel = characteristic.ProtectionLevel.ToString(),
            ReadStatus = readStatus,
            ValueHex = valueHex,
            ValueByteLength = valueLength,
            DescriptorCount = descriptors.Count,
            DurationMilliseconds = timer.ElapsedMilliseconds,
            Error = error
        });
        if (error is not null)
        {
            serviceErrors.Add($"{characteristic.Uuid}: {error}");
        }

        return new GattCharacteristicSnapshot(
            key,
            WindowsGattNames.Characteristic(characteristic.Uuid.ToString("D")),
            MapProperties(characteristic.CharacteristicProperties),
            characteristic.ProtectionLevel.ToString(),
            readStatus,
            valueHex,
            valueLength,
            error,
            timer.ElapsedMilliseconds,
            descriptors);
    }

    private async Task<GattDescriptorSnapshot> ReadDescriptorAsync(
        GattDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        string readStatus = "NotAttempted";
        string? valueHex = null;
        int? length = null;
        string? error = null;
        try
        {
            var result = await WithTimeout(descriptor.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken), cancellationToken);
            readStatus = result.Status.ToString();
            if (result.Status == GattCommunicationStatus.Success)
            {
                valueHex = ReadBuffer(result.Value);
                length = checked((int)result.Value.Length);
            }
            else
            {
                error = result.Status.ToString();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TimeoutException)
        {
            readStatus = "Error";
            error = exception.Message;
        }

        timer.Stop();
        await LogSafeAsync(error is null ? "Information" : "Warning", "GATT descriptor okundu.", new
        {
            Uuid = descriptor.Uuid.ToString("D").ToUpperInvariant(),
            descriptor.AttributeHandle,
            ReadStatus = readStatus,
            ValueHex = valueHex,
            ValueByteLength = length,
            DurationMilliseconds = timer.ElapsedMilliseconds,
            Error = error
        });
        return new GattDescriptorSnapshot(
            descriptor.Uuid.ToString("D").ToUpperInvariant(),
            WindowsGattNames.Descriptor(descriptor.Uuid.ToString("D")),
            descriptor.AttributeHandle,
            readStatus,
            valueHex,
            length,
            error,
            timer.ElapsedMilliseconds);
    }

    public async Task<GattOperationResult> SetNotificationsAsync(
        GattAttributeKey key,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        var timer = Stopwatch.StartNew();
        try
        {
            ThrowIfDisposed();
            if (!_characteristics.TryGetValue(key, out var characteristic))
            {
                return new GattOperationResult(false, "NotFound", "Characteristic bu bağlantı için keşfedilmedi.", 0);
            }

            if (enabled && !characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) &&
                !characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
            {
                return new GattOperationResult(false, "Unsupported", "Characteristic Notify veya Indicate özelliği sunmuyor.", 0);
            }

            characteristic.ValueChanged -= OnValueChanged;
            if (enabled)
            {
                characteristic.ValueChanged += OnValueChanged;
            }

            var mode = !enabled
                ? GattClientCharacteristicConfigurationDescriptorValue.None
                : characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                    ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                    : GattClientCharacteristicConfigurationDescriptorValue.Indicate;

            var status = await WithTimeout(
                characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(mode).AsTask(cancellationToken),
                cancellationToken);
            timer.Stop();
            if (status != GattCommunicationStatus.Success && enabled)
            {
                characteristic.ValueChanged -= OnValueChanged;
            }

            if (status == GattCommunicationStatus.Success)
            {
                if (enabled) _indicationModes[key] = mode == GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                else _indicationModes.Remove(key);
            }

            await LogSafeAsync(status == GattCommunicationStatus.Success ? "Information" : "Error",
                "Characteristic CCCD aboneliği işlendi.", new
                {
                    Key = key,
                    Requested = enabled,
                    CccdValue = mode.ToString(),
                    Status = status.ToString(),
                    DurationMilliseconds = timer.ElapsedMilliseconds
                });

            return new GattOperationResult(status == GattCommunicationStatus.Success, status.ToString(),
                status == GattCommunicationStatus.Success ? null : "CCCD aboneliği Windows BLE API tarafından reddedildi.",
                timer.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timer.Stop();
            return new GattOperationResult(false, "Error", exception.Message, timer.ElapsedMilliseconds);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<GattOperationResult> SetPowerAsync(bool on, CancellationToken cancellationToken = default) =>
        WriteHueValueAsync(
            HueLightControlProtocol.PowerCharacteristicUuid,
            [on ? HueLightControlProtocol.PowerOnValue : HueLightControlProtocol.PowerOffValue],
            "güç",
            new HueLightState(on, null, null, null, null),
            cancellationToken);

    public Task<GattOperationResult> SetBrightnessAsync(byte brightness, CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(brightness, HueLightControlProtocol.BrightnessMinimum, HueLightControlProtocol.BrightnessMaximum);
        return WriteHueValueAsync(
            HueLightControlProtocol.BrightnessCharacteristicUuid,
            [clamped],
            "parlaklık",
            new HueLightState(null, clamped, null, null, null),
            cancellationToken);
    }

    public Task<GattOperationResult> SetColorTemperatureAsync(ushort mired, CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(mired, HueLightControlProtocol.ColorTemperatureMinimumMired, HueLightControlProtocol.ColorTemperatureMaximumMired);
        return WriteHueValueAsync(
            HueLightControlProtocol.ColorTemperatureCharacteristicUuid,
            [(byte)(clamped & 0xFF), (byte)(clamped >> 8)],
            "renk sıcaklığı",
            new HueLightState(null, null, clamped, 0, 0),
            cancellationToken);
    }

    public Task<GattOperationResult> SetColorAsync(ushort x, ushort y, CancellationToken cancellationToken = default) =>
        WriteHueValueAsync(
            HueLightControlProtocol.ColorCharacteristicUuid,
            [(byte)(x & 0xFF), (byte)(x >> 8), (byte)(y & 0xFF), (byte)(y >> 8)],
            "renk",
            new HueLightState(null, null, 0xFFFF, x, y),
            cancellationToken);

    public Task<GattOperationResult> SetColorAndBrightnessAsync(ushort x, ushort y, byte brightness, CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(brightness, HueLightControlProtocol.BrightnessMinimum, HueLightControlProtocol.BrightnessMaximum);
        return WriteHueValueAsync(
            HueLightControlProtocol.CombinedStateCharacteristicUuid,
            HueLightControlProtocol.GetBrightnessAndColorValue(clamped, x, y),
            "renk ve parlaklık",
            new HueLightState(null, clamped, 0xFFFF, x, y),
            cancellationToken);
    }

    public Task<GattOperationResult> SetEffectAsync(HueEffect effect, byte speed, CancellationToken cancellationToken = default)
    {
        var clampedSpeed = Math.Clamp(speed, (byte)1, (byte)254);
        return WriteHueValueAsync(
            HueLightControlProtocol.CombinedStateCharacteristicUuid,
            HueLightControlProtocol.GetEffectValue(effect, clampedSpeed),
            "efekt",
            new HueLightState(null, null, null, null, null, (byte)effect, clampedSpeed),
            cancellationToken);
    }

    public Task<GattOperationResult> SetEffectSpeedAsync(byte speed, CancellationToken cancellationToken = default)
    {
        var clampedSpeed = Math.Clamp(speed, (byte)1, (byte)254);
        return WriteHueValueAsync(
            HueLightControlProtocol.CombinedStateCharacteristicUuid,
            HueLightControlProtocol.GetEffectSpeedValue(clampedSpeed),
            "efekt hızı",
            new HueLightState(null, null, null, null, null, null, clampedSpeed),
            cancellationToken);
    }

    public Task<GattOperationResult> IdentifyAsync(CancellationToken cancellationToken = default) =>
        WriteHueValueAsync(
            HueLightControlProtocol.IdentifyCharacteristicUuid,
            [0x01],
            "identify",
            null,
            cancellationToken);

    public async Task<GattOperationResult> RefreshLightStateAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        var timer = Stopwatch.StartNew();
        try
        {
            ThrowIfDisposed();
            var characteristic = await FindHueCharacteristicAsync(HueLightControlProtocol.CombinedStateCharacteristicUuid, cancellationToken);
            if (characteristic is null)
            {
                timer.Stop();
                return new GattOperationResult(false, "NotFound", "Ampulde birleşik durum characteristic'i bulunamadı.", timer.ElapsedMilliseconds);
            }

            await EnsureLightStateNotificationsAsync(characteristic, cancellationToken);

            var read = await WithTimeout(
                characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken),
                cancellationToken);
            timer.Stop();
            if (read.Status != GattCommunicationStatus.Success)
            {
                return new GattOperationResult(false, read.Status.ToString(), DescribeReadError(read.Status), timer.ElapsedMilliseconds);
            }

            var payload = ReadBytes(read.Value);
            var state = HueLightStateParser.ParseCombined(payload);
            LightStateChanged?.Invoke(this, new LightStateChangedEventArgs(state));
            await LogSafeAsync("Information", "Ampul durumu okundu.", new { Device = Device.Address, State = state });
            return new GattOperationResult(true, read.Status.ToString(), null, timer.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timer.Stop();
            return new GattOperationResult(false, "Error", exception.Message, timer.ElapsedMilliseconds);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<HueDeviceInfo?> ReadDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var manufacturer = DecodeUtf8(await ReadCharacteristicAsync(HueLightControlProtocol.DeviceInformationServiceUuid, HueLightControlProtocol.ManufacturerNameCharacteristicUuid, cancellationToken));
            var model = DecodeUtf8(await ReadCharacteristicAsync(HueLightControlProtocol.DeviceInformationServiceUuid, HueLightControlProtocol.ModelNumberCharacteristicUuid, cancellationToken));
            var software = DecodeUtf8(await ReadCharacteristicAsync(HueLightControlProtocol.DeviceInformationServiceUuid, HueLightControlProtocol.SoftwareRevisionCharacteristicUuid, cancellationToken));
            var zigbee = FormatZigbeeAddress(await ReadCharacteristicAsync(HueLightControlProtocol.HueConfigurationServiceUuid, HueLightControlProtocol.ZigbeeAddressCharacteristicUuid, cancellationToken));
            var name = DecodeUtf8(await ReadCharacteristicAsync(HueLightControlProtocol.HueConfigurationServiceUuid, HueLightControlProtocol.DeviceNameCharacteristicUuid, cancellationToken));

            var info = new HueDeviceInfo(model, software, manufacturer, zigbee, name);
            await LogSafeAsync("Information", "Ampul cihaz bilgileri okundu.", new { Device = Device.Address, Info = info });
            return info;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogSafeAsync("Warning", "Ampul cihaz bilgileri okunamadı.", new { Device = Device.Address, exception.Message });
            return null;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<GattOperationResult> SetDeviceNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length > HueLightControlProtocol.DeviceNameMaximumCharacters)
        {
            trimmed = trimmed[..HueLightControlProtocol.DeviceNameMaximumCharacters];
        }

        if (trimmed.Length == 0)
        {
            return Task.FromResult(new GattOperationResult(false, "Invalid", "Cihaz adı boş olamaz.", 0));
        }

        return WriteCharacteristicAsync(
            HueLightControlProtocol.HueConfigurationServiceUuid,
            HueLightControlProtocol.DeviceNameCharacteristicUuid,
            Encoding.UTF8.GetBytes(trimmed),
            "cihaz adı",
            ensureLightStateNotifications: false,
            cancellationToken);
    }

    public async Task<HuePowerOnBehaviour?> ReadPowerOnBehaviourAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var payload = await ReadCharacteristicAsync(HueLightControlProtocol.LightControlServiceUuid, HueLightControlProtocol.PowerOnBehaviourCharacteristicUuid, cancellationToken);
            if (payload is null || payload.Length == 0)
            {
                return null;
            }

            var behaviour = HueLightControlProtocol.ParsePowerOnBehaviour(payload);
            await LogSafeAsync("Information", "Ampul açılış davranışı okundu.", new { Device = Device.Address, Behaviour = behaviour.ToString(), Payload = Convert.ToHexString(payload) });
            return behaviour;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogSafeAsync("Warning", "Ampul açılış davranışı okunamadı.", new { Device = Device.Address, exception.Message });
            return null;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<GattOperationResult> SetPowerOnBehaviourAsync(HuePowerOnBehaviour behaviour, CancellationToken cancellationToken = default) =>
        WriteCharacteristicAsync(
            HueLightControlProtocol.LightControlServiceUuid,
            HueLightControlProtocol.PowerOnBehaviourCharacteristicUuid,
            HueLightControlProtocol.GetPowerOnBehaviourValue(behaviour),
            "açılış davranışı",
            ensureLightStateNotifications: false,
            cancellationToken);

    private async Task<GattOperationResult> WriteHueValueAsync(
        string characteristicUuid,
        byte[] payload,
        string label,
        HueLightState? optimisticState,
        CancellationToken cancellationToken)
    {
        var result = await WriteCharacteristicAsync(
            HueLightControlProtocol.LightControlServiceUuid,
            characteristicUuid,
            payload,
            label,
            ensureLightStateNotifications: true,
            cancellationToken);
        if (result.IsSuccess && optimisticState is not null)
        {
            LightStateChanged?.Invoke(this, new LightStateChangedEventArgs(optimisticState));
        }

        return result;
    }

    private async Task<GattOperationResult> WriteCharacteristicAsync(
        string serviceUuid,
        string characteristicUuid,
        byte[] payload,
        string label,
        bool ensureLightStateNotifications,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        var timer = Stopwatch.StartNew();
        try
        {
            ThrowIfDisposed();
            var characteristic = await FindCharacteristicAsync(serviceUuid, characteristicUuid, cancellationToken);
            if (characteristic is null)
            {
                timer.Stop();
                return new GattOperationResult(false, "NotFound", $"Ampulde {label} characteristic'i bulunamadı.", timer.ElapsedMilliseconds);
            }

            if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write))
            {
                timer.Stop();
                return new GattOperationResult(false, "Unsupported", $"Ampul {label} characteristic'i yanıtlı yazmayı desteklemiyor.", timer.ElapsedMilliseconds);
            }

            if (ensureLightStateNotifications)
            {
                await EnsureLightStateNotificationsAsync(null, cancellationToken);
            }

            var writer = new DataWriter();
            writer.WriteBytes(payload);
            var result = await WithTimeout(
                characteristic.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask(cancellationToken),
                cancellationToken);
            timer.Stop();

            var success = result.Status == GattCommunicationStatus.Success;
            await LogSafeAsync(success ? "Information" : "Error", $"Ampul {label} komutu gönderildi.", new
            {
                Device = Device.Address,
                Payload = Convert.ToHexString(payload),
                Status = result.Status.ToString(),
                ProtocolError = result.ProtocolError,
                DurationMilliseconds = timer.ElapsedMilliseconds
            });

            return new GattOperationResult(success, result.Status.ToString(), success ? null : DescribeWriteError(result), timer.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timer.Stop();
            return new GattOperationResult(false, "Error", exception.Message, timer.ElapsedMilliseconds);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<byte[]?> ReadCharacteristicAsync(string serviceUuid, string characteristicUuid, CancellationToken cancellationToken)
    {
        var characteristic = await FindCharacteristicAsync(serviceUuid, characteristicUuid, cancellationToken);
        if (characteristic is null)
        {
            return null;
        }

        var read = await WithTimeout(characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken), cancellationToken);
        if (read.Status != GattCommunicationStatus.Success)
        {
            await LogSafeAsync("Warning", "Ampul characteristic'i okunamadı.", new { Device = Device.Address, Uuid = characteristicUuid, Status = read.Status.ToString() });
            return null;
        }

        return ReadBytes(read.Value);
    }

    private static string? DecodeUtf8(byte[]? bytes) =>
        bytes is null ? null : Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();

    private static string? FormatZigbeeAddress(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 8)
        {
            return null;
        }

        var reversed = bytes.Take(8).Reverse().ToArray();
        return string.Join(':', reversed.Select(value => value.ToString("X2")));
    }

    private Task<GattCharacteristic?> FindHueCharacteristicAsync(string characteristicUuid, CancellationToken cancellationToken) =>
        FindCharacteristicAsync(HueLightControlProtocol.LightControlServiceUuid, characteristicUuid, cancellationToken);

    private async Task<GattCharacteristic?> FindCharacteristicAsync(string serviceUuid, string characteristicUuid, CancellationToken cancellationToken)
    {
        if (_hueCharacteristics.TryGetValue(characteristicUuid, out var cached))
        {
            return cached;
        }

        foreach (var pair in _characteristics)
        {
            if (string.Equals(pair.Key.CharacteristicUuid, characteristicUuid, StringComparison.OrdinalIgnoreCase))
            {
                _hueCharacteristics[characteristicUuid] = pair.Value;
                return pair.Value;
            }
        }

        var services = await WithTimeout(
            _device.GetGattServicesForUuidAsync(new Guid(serviceUuid), BluetoothCacheMode.Uncached).AsTask(cancellationToken),
            cancellationToken);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            await LogSafeAsync("Warning", "Ampulde GATT servisi bulunamadı.", new { Device = Device.Address, Service = serviceUuid, Status = services.Status.ToString() });
            return null;
        }

        var service = services.Services[0];
        var characteristics = await WithTimeout(
            service.GetCharacteristicsForUuidAsync(new Guid(characteristicUuid), BluetoothCacheMode.Uncached).AsTask(cancellationToken),
            cancellationToken);
        if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
        {
            await LogSafeAsync("Warning", "Ampulde Hue characteristic'i bulunamadı.", new { Device = Device.Address, Uuid = characteristicUuid, Status = characteristics.Status.ToString() });
            return null;
        }

        var characteristic = characteristics.Characteristics[0];
        _hueCharacteristics[characteristicUuid] = characteristic;
        _characteristics[new GattAttributeKey(
            service.Uuid.ToString("D").ToUpperInvariant(),
            service.AttributeHandle,
            characteristic.Uuid.ToString("D").ToUpperInvariant(),
            characteristic.AttributeHandle)] = characteristic;
        return characteristic;
    }

    private async Task EnsureLightStateNotificationsAsync(GattCharacteristic? knownCharacteristic, CancellationToken cancellationToken)
    {
        if (_lightStateSubscribed)
        {
            return;
        }

        try
        {
            var characteristic = knownCharacteristic ?? await FindHueCharacteristicAsync(HueLightControlProtocol.CombinedStateCharacteristicUuid, cancellationToken);
            if (characteristic is null)
            {
                return;
            }

            characteristic.ValueChanged += OnCombinedStateChanged;
            var status = await WithTimeout(
                characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken),
                cancellationToken);
            if (status == GattCommunicationStatus.Success)
            {
                _lightStateCharacteristic = characteristic;
                _lightStateSubscribed = true;
                return;
            }

            characteristic.ValueChanged -= OnCombinedStateChanged;
            await LogSafeAsync("Warning", "Ampul durum bildirimlerine abone olunamadı.", new { Device = Device.Address, Status = status.ToString() });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogSafeAsync("Warning", "Ampul durum bildirim aboneliği kurulamadı.", new { Device = Device.Address, exception.Message });
        }
    }

    private void OnCombinedStateChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var payload = ReadBytes(args.CharacteristicValue);
            var state = HueLightStateParser.ParseCombined(payload);
            LightStateChanged?.Invoke(this, new LightStateChangedEventArgs(state));
            _ = LogSafeAsync("Information", "Ampul durumu bildirildi.", new { Device = Device.Address, State = state });
        }
        catch (Exception exception)
        {
            _ = LogSafeAsync("Warning", "Ampul durum bildirimi işlenemedi.", new { Device = Device.Address, exception.Message });
        }
    }

    private static string DescribeWriteError(GattWriteResult result)
    {
        if (result.ProtocolError == 0x05)
        {
            return "Ampul komutu reddetti: şifreli bağ gerekiyor (Insufficient Authentication). Ampulü Windows ile eşleştirin.";
        }

        return $"Ampul komutu reddedildi: {result.Status}.";
    }

    private static string DescribeReadError(GattCommunicationStatus status)
    {
        if (status == GattCommunicationStatus.ProtocolError)
        {
            return "Ampul durumunu okumak için şifreli bağ gerekiyor. Ampulü Windows ile eşleştirin.";
        }

        return $"Ampul durumu okunamadı: {status}.";
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var key = _characteristics.FirstOrDefault(item => ReferenceEquals(item.Value, sender)).Key;
            if (key is null)
            {
                return;
            }

            NotificationReceived?.Invoke(this, new GattNotificationEventArgs(new GattNotificationEvent(
                key,
                DateTimeOffset.UtcNow,
                ReadBuffer(args.CharacteristicValue),
                checked((int)args.CharacteristicValue.Length),
                _indicationModes.GetValueOrDefault(key))));
            _ = LogSafeAsync("Information", "GATT bildirimi alındı.", new
            {
                Key = key,
                ValueHex = ReadBuffer(args.CharacteristicValue),
                ValueByteLength = args.CharacteristicValue.Length,
                IsIndication = _indicationModes.GetValueOrDefault(key)
            });
        }
        catch (Exception exception)
        {
            NotificationReceived?.Invoke(this, new GattNotificationEventArgs(new GattNotificationEvent(
                new GattAttributeKey("", 0, "", 0), DateTimeOffset.UtcNow, $"<notification error: {exception.Message}>", 0, false)));
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(sender.ConnectionStatus.ToString()));
        _ = LogSafeAsync("Information", "BLE bağlantı durumu değişti.", new { Device = Device.Address, State = sender.ConnectionStatus.ToString() });
    }

    private void OnSessionStatusChanged(GattSession sender, object args)
    {
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState));
        _ = LogSafeAsync("Information", "Windows Bluetooth oturum durumu değişti.", new
        {
            Device = Device.Address,
            SessionState = sender.SessionStatus.ToString(),
            ConnectionState
        });
    }

    private static GattCharacteristicOperations MapProperties(GattCharacteristicProperties properties)
    {
        var mapped = GattCharacteristicOperations.None;
        if (properties.HasFlag(GattCharacteristicProperties.Broadcast)) mapped |= GattCharacteristicOperations.Broadcast;
        if (properties.HasFlag(GattCharacteristicProperties.Read)) mapped |= GattCharacteristicOperations.Read;
        if (properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)) mapped |= GattCharacteristicOperations.WriteWithoutResponse;
        if (properties.HasFlag(GattCharacteristicProperties.Write)) mapped |= GattCharacteristicOperations.Write;
        if (properties.HasFlag(GattCharacteristicProperties.Notify)) mapped |= GattCharacteristicOperations.Notify;
        if (properties.HasFlag(GattCharacteristicProperties.Indicate)) mapped |= GattCharacteristicOperations.Indicate;
        if (properties.HasFlag(GattCharacteristicProperties.AuthenticatedSignedWrites)) mapped |= GattCharacteristicOperations.SignedWrites;
        if (properties.HasFlag(GattCharacteristicProperties.ExtendedProperties)) mapped |= GattCharacteristicOperations.ExtendedProperties;
        return mapped;
    }

    private static string ReadBuffer(IBuffer buffer)
    {
        if (buffer.Length == 0)
        {
            return string.Empty;
        }

        return Convert.ToHexString(ReadBytes(buffer));
    }

    private static byte[] ReadBytes(IBuffer buffer)
    {
        if (buffer.Length == 0)
        {
            return [];
        }

        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }

    private static string? Append(string? existing, string addition)
    {
        return existing is null ? addition : $"{existing} {addition}";
    }

    private async Task<T> WithTimeout<T>(Task<T> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation.WaitAsync(OperationTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _unusable = true;
            _device.Dispose();
            await LogSafeAsync("Error", "GATT işlemi zaman aşımına uğradı; bu oturum kapatıldı.", new { Device = Device.Address, TimeoutSeconds = OperationTimeout.TotalSeconds });
            throw new TimeoutException("Windows GATT işlemi 15 saniye içinde yanıt vermedi. Cihaz yeniden bağlanmayı gerektirebilir.");
        }
    }

    private async Task LogSafeAsync(string level, string message, object? data = null)
    {
        try { await _logger.WriteAsync(level, message, data); }
        catch { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed || _unusable, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _operationLock.WaitAsync();
        try
        {
            foreach (var characteristic in _characteristics.Values.Distinct())
            {
                characteristic.ValueChanged -= OnValueChanged;
            }

            _lightStateCharacteristic?.ValueChanged -= OnCombinedStateChanged;
            _lightStateSubscribed = false;

            _characteristics.Clear();
            _indicationModes.Clear();
            _hueCharacteristics.Clear();
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _session.SessionStatusChanged -= OnSessionStatusChanged;
            _session.MaintainConnection = false;
            _session.Dispose();
            _device.Dispose();
            await LogSafeAsync("Information", "BLE bağlantı kaynağı bırakıldı.", new { Device = Device.Address });
        }
        finally
        {
            _operationLock.Release();
            _operationLock.Dispose();
        }
    }
}
