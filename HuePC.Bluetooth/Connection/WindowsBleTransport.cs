using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using HuePC.Bluetooth.Gatt;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;

namespace HuePC.Bluetooth.Connection;

public sealed class WindowsBleTransport : IBleTransport
{
    private static readonly TimeSpan ConnectionAttemptTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UnpairedInquiryTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan CacheRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CacheRetryLimit = TimeSpan.FromSeconds(50);
    private readonly IDiagnosticLogger _logger;

    public WindowsBleTransport(IDiagnosticLogger logger)
    {
        _logger = logger;
    }

    public async Task<IBleConnection> ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await LogSafeAsync("Information", "BLE bağlantısı başlatıldı.", device);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectionAttemptTimeout);

            var bluetoothDevice = await ResolveDeviceObjectAsync(device, timeout.Token);
            if (bluetoothDevice is null)
            {
                throw new InvalidOperationException(
                    "Windows Hue BLE cihaz nesnesini oluşturamadı. Ampulü açık ve yakında tutup yeniden deneyin.");
            }

            var session = await GattSession.FromDeviceIdAsync(bluetoothDevice.BluetoothDeviceId)
                .AsTask(timeout.Token)
                .WaitAsync(ConnectionAttemptTimeout, cancellationToken);
            if (session is null)
            {
                bluetoothDevice.Dispose();
                throw new InvalidOperationException("Windows Bluetooth oturumu oluşturamadı.");
            }

            if (!session.CanMaintainConnection)
            {
                session.Dispose();
                bluetoothDevice.Dispose();
                throw new InvalidOperationException("Windows bu cihaz için kalıcı Bluetooth bağlantısını desteklemiyor.");
            }

            // Ask Windows to connect when the bulb is available and keep the session alive.
            // This deliberately does not enumerate GATT services or characteristics.
            session.MaintainConnection = true;

            var connection = new WindowsBleConnection(device, bluetoothDevice, session, _logger);
            await LogSafeAsync("Information", "BLE cihaz nesnesi oluşturuldu.", new { device.Address, connection.ConnectionState, connection.IsPaired });
            return connection;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogSafeAsync("Error", "BLE bağlantısı kurulamadı.", new
            {
                device.Address,
                exception.Message,
                HResult = $"0x{unchecked((uint)exception.HResult):X8}",
                ExceptionChain = GetExceptionChain(exception)
            });
            throw new InvalidOperationException(DescribeBluetoothError(exception), exception);
        }
    }

    /// <summary>
    /// Windows resolves an unpaired BLE address only when the device already exists in its device
    /// cache. The cache is seeded by the unpaired-device inquiry, which surfaces devices around the
    /// 30 second mark. The fast address path is tried first so cached devices connect instantly;
    /// otherwise the transport runs the full inquiry and retries the address path on the warm cache.
    /// </summary>
    private async Task<BluetoothLEDevice?> ResolveDeviceObjectAsync(BleDeviceInfo device, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        var direct = await TryCreateFromLiveAddressAsync(device, cancellationToken, logErrors: true);
        if (direct is not null)
        {
            return direct;
        }

        await LogSafeAsync("Information", "Hue ampulü Windows cihaz önbelleğinde bulunamadı; eşleştirilmemiş cihaz sorgusu başlatıldı.", new
        {
            device.Address,
            device.AddressType,
            InquiryTimeoutSeconds = UnpairedInquiryTimeout.TotalSeconds
        });
        var endpoint = await WaitForUnpairedEndpointAsync(device.Address, UnpairedInquiryTimeout, cancellationToken);
        if (endpoint is not null)
        {
            var fromEndpoint = await TryCreateFromEndpointAsync(endpoint, cancellationToken);
            if (fromEndpoint is not null)
            {
                return fromEndpoint;
            }
        }

        // The inquiry seeds Windows' device cache even when endpoint creation itself failed.
        while (started.Elapsed < CacheRetryLimit)
        {
            await Task.Delay(CacheRetryDelay, cancellationToken);
            var retry = await TryCreateFromLiveAddressAsync(device, cancellationToken, logErrors: false);
            if (retry is not null)
            {
                await LogSafeAsync("Information", "Hue ampulü önbellek sorgusu sonrasında canlı adresten açıldı.", new
                {
                    device.Address,
                    ElapsedMilliseconds = started.ElapsedMilliseconds
                });
                return retry;
            }
        }

        await LogSafeAsync("Warning", "Windows Hue ampulü için BLE cihaz nesnesini hiçbir yoldan oluşturamadı.", new
        {
            device.Address,
            device.AddressType,
            ElapsedMilliseconds = started.ElapsedMilliseconds
        });
        return null;
    }

    private async Task<BluetoothLEDevice?> TryCreateFromLiveAddressAsync(BleDeviceInfo device, CancellationToken cancellationToken, bool logErrors)
    {
        foreach (var addressType in GetAddressTypeCandidates(device.AddressType))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestedType = addressType?.ToString() ?? "Windows varsayılanı";
            try
            {
                var candidate = addressType is null
                    ? await BluetoothLEDevice.FromBluetoothAddressAsync(device.BluetoothAddress).AsTask(cancellationToken)
                    : await BluetoothLEDevice.FromBluetoothAddressAsync(device.BluetoothAddress, addressType.Value).AsTask(cancellationToken);
                if (candidate is not null)
                {
                    await LogSafeAsync("Information", "Hue ampulü canlı BLE adresinden açıldı.", new
                    {
                        device.Address,
                        RequestedAddressType = requestedType,
                        candidate.ConnectionStatus,
                        IsPaired = candidate.DeviceInformation.Pairing.IsPaired
                    });
                    return candidate;
                }

                if (logErrors)
                {
                    await LogSafeAsync("Warning", "Windows canlı Hue reklam adresi için null döndürdü.", new
                    {
                        device.Address,
                        device.AddressType,
                        RequestedAddressType = requestedType
                    });
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (logErrors)
                {
                    await LogSafeAsync("Warning", "Windows canlı Hue reklamından Bluetooth cihaz nesnesi oluşturamadı.", new
                    {
                        device.Address,
                        device.AddressType,
                        RequestedAddressType = requestedType,
                        exception.Message,
                        exception.HResult
                    });
                }
            }
        }

        return null;
    }

    private async Task<BluetoothLEDevice?> TryCreateFromEndpointAsync(DeviceInformation endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var device = await BluetoothLEDevice.FromIdAsync(endpoint.Id).AsTask(cancellationToken);
            if (device is null)
            {
                await LogSafeAsync("Warning", "Windows eşleştirilmemiş LE endpoint'ini Bluetooth cihazına dönüştüremedi.", new { endpoint.Id, endpoint.Name });
                return null;
            }

            await LogSafeAsync("Information", "Hue ampulü eşleştirilmemiş LE endpoint'inden açıldı.", new
            {
                endpoint.Id,
                endpoint.Name
            });
            return device;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await LogSafeAsync("Warning", "Windows eşleştirilmemiş LE endpoint cihazı açılamadı.", new
            {
                endpoint.Id,
                endpoint.Name,
                exception.Message,
                exception.HResult
            });
            return null;
        }
    }

    private static IReadOnlyList<BluetoothAddressType?> GetAddressTypeCandidates(string? addressType)
    {
        var observed = addressType?.Trim().ToLowerInvariant() switch
        {
            "random" => BluetoothAddressType.Random,
            "public" => BluetoothAddressType.Public,
            _ => (BluetoothAddressType?)null
        };
        var opposite = observed switch
        {
            BluetoothAddressType.Random => BluetoothAddressType.Public,
            BluetoothAddressType.Public => BluetoothAddressType.Random,
            _ => (BluetoothAddressType?)null
        };

        var candidates = new List<BluetoothAddressType?>();
        if (observed is not null) candidates.Add(observed);
        if (opposite is not null) candidates.Add(opposite);
        candidates.Add(null);
        return candidates;
    }

    private static IReadOnlyList<object> GetExceptionChain(Exception exception)
    {
        var chain = new List<object>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            chain.Add(new
            {
                Type = current.GetType().FullName,
                current.Message,
                HResult = $"0x{unchecked((uint)current.HResult):X8}"
            });
        }

        return chain;
    }

    private static string DescribeBluetoothError(Exception exception)
    {
        var hresult = unchecked((uint)exception.HResult);
        var hint = hresult switch
        {
            0x800710DF => "Bluetooth cihazı bulunamadı veya başka bir uygulama tarafından kullanılıyor.",
            0x80070005 => "Windows Bluetooth erişimini reddetti. Windows izinlerini ve Bluetooth ayarlarını kontrol edin.",
            0x8007048F => "Bluetooth aygıtı kapalı veya erişilemiyor.",
            _ => exception.Message
        };
        return $"{hint} (HRESULT 0x{hresult:X8})";
    }

    private static async Task<DeviceInformation?> WaitForUnpairedEndpointAsync(string targetAddress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        const string deviceAddressProperty = "System.Devices.Aep.DeviceAddress";
        var result = new TaskCompletionSource<DeviceInformation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(false);
        var watcher = DeviceInformation.CreateWatcher(
            selector,
            new[] { deviceAddressProperty },
            DeviceInformationKind.AssociationEndpoint);
        var expectedAddress = NormalizeAddress(targetAddress);
        void OnAdded(DeviceWatcher _, DeviceInformation information)
        {
            if (information.Properties.TryGetValue(deviceAddressProperty, out var value) &&
                value is string address &&
                string.Equals(NormalizeAddress(address), expectedAddress, StringComparison.OrdinalIgnoreCase))
            {
                result.TrySetResult(information);
            }
        }

        watcher.Added += OnAdded;
        try
        {
            watcher.Start();
            try
            {
                return await result.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
        finally
        {
            watcher.Added -= OnAdded;
            try { watcher.Stop(); }
            catch { }
        }
    }

    private static string NormalizeAddress(string address) =>
        new(address.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    private async Task LogSafeAsync(string level, string message, object? data = null)
    {
        try { await _logger.WriteAsync(level, message, data); }
        catch { }
    }
}
