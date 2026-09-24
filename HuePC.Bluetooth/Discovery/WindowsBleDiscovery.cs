using System.Globalization;
using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;
using Windows.Storage.Streams;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;
using HuePC.Core.Services;

namespace HuePC.Bluetooth.Discovery;

public sealed class WindowsBleDiscovery : IDeviceDiscovery
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly IDiagnosticLogger _logger;
    private readonly ConcurrentDictionary<ulong, (DateTimeOffset LoggedAt, int Rssi)> _lastLoggedAdvertisements = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private bool _disposed;

    public event EventHandler<DiscoveredDeviceEventArgs>? DeviceDiscovered;
    public event EventHandler<string>? ScanError;

    public WindowsBleDiscovery(IDiagnosticLogger logger)
    {
        _logger = logger;
    }

    public async Task<AdapterStatus> GetAdapterStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                return new AdapterStatus(false, false, false, "Bluetooth adaptörü bulunamadı.");
            }

            var radios = await Radio.GetRadiosAsync();
            var bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            var enabled = bluetoothRadio?.State == RadioState.On;
            var description = !adapter.IsLowEnergySupported
                ? "Bluetooth bağdaştırıcısı Bluetooth LE desteği sunmuyor."
                : bluetoothRadio is null
                    ? "Bluetooth radyosu durum bilgisi alınamadı."
                    : enabled
                        ? "Bluetooth LE hazır."
                        : "Bluetooth kapalı.";

            var result = new AdapterStatus(true, enabled, adapter.IsLowEnergySupported, description);
            await LogSafeAsync("Information", "Bluetooth bağdaştırıcısı denetlendi.", result);
            return result;
        }
        catch (Exception exception)
        {
            var result = new AdapterStatus(false, false, false, "Bluetooth durumu okunamadı.", exception.Message);
            await LogSafeAsync("Error", "Bluetooth bağdaştırıcısı denetlenemedi.", new { exception.Message, exception.HResult });
            return result;
        }
    }

    public async Task StartScanningAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            {
                return;
            }

            var adapterStatus = await GetAdapterStatusAsync(cancellationToken);
            if (!adapterStatus.IsAvailable || !adapterStatus.IsLowEnergySupported)
            {
                throw new InvalidOperationException(adapterStatus.Error ?? adapterStatus.Description);
            }

            if (!adapterStatus.IsBluetoothEnabled)
            {
                throw new InvalidOperationException("Bluetooth kapalı. Windows Ayarları'ndan Bluetooth'u açıp yeniden deneyin.");
            }

            _watcher ??= CreateWatcher();
            _watcher.Start();
            await LogSafeAsync("Information", "BLE taraması başlatıldı.", new { Mode = "Active", Adapter = adapterStatus.Description });
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopScanningAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            _watcher?.Stop();
            await LogSafeAsync("Information", "BLE taraması durduruldu.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private BluetoothLEAdvertisementWatcher CreateWatcher()
    {
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        watcher.Received += OnAdvertisementReceived;
        watcher.Stopped += OnWatcherStopped;
        return watcher;
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            var name = args.Advertisement.LocalName?.Trim() ?? string.Empty;
            var hasSignifyCompanyId = args.Advertisement.ManufacturerData
                .Any(entry => entry.CompanyId == HueAdvertisementMatcher.SignifyCompanyIdentifier);
            var serviceUuids = args.Advertisement.ServiceUuids
                .Select(uuid => uuid.ToString("D").ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Discard unrelated BLE advertisements before building models, raising events, or logging.
            var match = HueAdvertisementMatcher.Match(name, hasSignifyCompanyId, serviceUuids);
            if (!match.IsCandidate)
            {
                return;
            }

            var manufacturerData = args.Advertisement.ManufacturerData
                .Select(entry => new ManufacturerDataEntry(entry.CompanyId, ReadBuffer(entry.Data)))
                .ToArray();

            var device = new BleDeviceInfo(
                args.BluetoothAddress,
                FormatAddress(args.BluetoothAddress),
                args.BluetoothAddressType.ToString(),
                name,
                args.RawSignalStrengthInDBm,
                DateTimeOffset.UtcNow,
                serviceUuids,
                manufacturerData,
                args.IsConnectable,
                true)
            {
                HueMatchReason = match.Reason
            };

            DeviceDiscovered?.Invoke(this, new DiscoveredDeviceEventArgs(device));
            if (ShouldLogAdvertisement(device))
            {
                _ = LogSafeAsync("Information", "Hue odaklı BLE reklamı eşleşti.", device);
            }
        }
        catch (Exception exception)
        {
            ScanError?.Invoke(this, $"BLE reklamı işlenemedi: {exception.Message}");
            _ = LogSafeAsync("Error", "BLE reklamı işlenemedi.", new { exception.Message, exception.HResult });
        }
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != BluetoothError.Success)
        {
            ScanError?.Invoke(this, $"BLE taraması durdu: {args.Error}.");
            _ = LogSafeAsync("Error", "BLE izleyici hata ile durdu.", new { Error = args.Error.ToString(), Status = sender.Status.ToString() });
        }
    }

    private bool ShouldLogAdvertisement(BleDeviceInfo device)
    {
        while (true)
        {
            if (!_lastLoggedAdvertisements.TryGetValue(device.BluetoothAddress, out var previous))
            {
                if (_lastLoggedAdvertisements.TryAdd(device.BluetoothAddress, (device.LastSeenUtc, device.Rssi))) return true;
                continue;
            }

            if (device.LastSeenUtc - previous.LoggedAt < TimeSpan.FromSeconds(5) && Math.Abs(device.Rssi - previous.Rssi) < 8)
            {
                return false;
            }

            if (_lastLoggedAdvertisements.TryUpdate(device.BluetoothAddress, (device.LastSeenUtc, device.Rssi), previous)) return true;
        }
    }

    private async Task LogSafeAsync(string level, string message, object? data = null)
    {
        try
        {
            await _logger.WriteAsync(level, message, data);
        }
        catch
        {
            // Diagnostics must never stop scanning when the local disk cannot accept a log entry.
        }
    }

    private static string ReadBuffer(IBuffer buffer)
    {
        if (buffer.Length == 0)
        {
            return string.Empty;
        }

        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return Convert.ToHexString(bytes);
    }

    private static string FormatAddress(ulong address)
    {
        var raw = address.ToString("X12", CultureInfo.InvariantCulture);
        return string.Join(':', Enumerable.Range(0, 6).Select(index => raw.Substring(index * 2, 2)));
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_watcher is not null)
            {
                _watcher.Stop();
                _watcher.Received -= OnAdvertisementReceived;
                _watcher.Stopped -= OnWatcherStopped;
                _watcher = null;
            }
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
        }
    }
}
