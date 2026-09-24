using HuePC.Bluetooth.Connection;
using HuePC.Bluetooth.Discovery;
using HuePC.Core.Interfaces;
using HuePC.Core.Models;
using HuePC.Core.Services;
using HuePC.Infrastructure.Logging;

await using var logger = new LocalDiagnosticLogger();
var deviceOptionIndex = Array.FindIndex(args, argument => string.Equals(argument, "--device", StringComparison.OrdinalIgnoreCase));
var requestedDevice = deviceOptionIndex >= 0 && deviceOptionIndex + 1 < args.Length
    ? args[deviceOptionIndex + 1]
    : null;
if (deviceOptionIndex >= 0 && (requestedDevice is null || requestedDevice.StartsWith("--", StringComparison.Ordinal)))
{
    Console.WriteLine("Usage: HuePC.HardwareProbe [--adapter] [--device <BLE address>]");
    return 2;
}

if (args.Contains("--adapter", StringComparer.OrdinalIgnoreCase))
{
    var windowsAdapter = await Windows.Devices.Bluetooth.BluetoothAdapter.GetDefaultAsync();
    if (windowsAdapter is null)
    {
        Console.WriteLine("No default Bluetooth adapter.");
        return 2;
    }
    Console.WriteLine($"LE={windowsAdapter.IsLowEnergySupported}; central={windowsAdapter.IsCentralRoleSupported}; peripheral={windowsAdapter.IsPeripheralRoleSupported}; classic={windowsAdapter.IsClassicSupported}");
    return 0;
}
await using var discovery = new WindowsBleDiscovery(logger);
var transport = new WindowsBleTransport(logger);
var candidates = new Dictionary<string, BleDeviceInfo>(StringComparer.OrdinalIgnoreCase);
var hueSignatureSamples = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var lastHueSignatureSample = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
var candidateLock = new object();

var adapter = await discovery.GetAdapterStatusAsync();
Console.WriteLine($"Adapter: {adapter.Description}; LE={adapter.IsLowEnergySupported}; enabled={adapter.IsBluetoothEnabled}");
if (!adapter.IsAvailable || !adapter.IsBluetoothEnabled || !adapter.IsLowEnergySupported)
{
    Console.WriteLine(adapter.Error ?? adapter.Description);
    return 2;
}

discovery.DeviceDiscovered += (_, eventArgs) =>
{
    var current = eventArgs.Device;
    lock (candidateLock)
    {
        var info = candidates.TryGetValue(current.DeviceKey, out var previous)
            ? MergeAdvertisements(previous, current)
            : current;
        candidates[info.DeviceKey] = info;

        if (current.AdvertisedServiceUuids.Contains(HueAdvertisementMatcher.SignifyMemberServiceUuid, StringComparer.OrdinalIgnoreCase) &&
            (!lastHueSignatureSample.TryGetValue(current.DeviceKey, out var lastSample) ||
             current.LastSeenUtc - lastSample >= TimeSpan.FromSeconds(3)))
        {
            hueSignatureSamples[current.DeviceKey] = hueSignatureSamples.GetValueOrDefault(current.DeviceKey) + 1;
            lastHueSignatureSample[current.DeviceKey] = current.LastSeenUtc;
        }
    }
};

await discovery.StartScanningAsync();
Console.WriteLine("Scanning for Hue / Signify advertisements until three stable 0xFE0F samples arrive (up to 30 seconds)...");
var scanDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
while (DateTimeOffset.UtcNow < scanDeadline)
{
    bool hasStableCandidate;
    lock (candidateLock)
        hasStableCandidate = hueSignatureSamples.Values.Any(sampleCount => sampleCount >= 3);
    if (hasStableCandidate) break;
    await Task.Delay(TimeSpan.FromMilliseconds(250));
}

BleDeviceInfo[] currentCandidates;
lock (candidateLock) currentCandidates = candidates.Values.OrderByDescending(candidate => candidate.Rssi).ToArray();
foreach (var candidate in currentCandidates)
{
    Console.WriteLine($"Candidate: {candidate.Address} ({candidate.AddressType}), RSSI {candidate.Rssi} dBm, name='{candidate.Name}', match={candidate.HueMatchReason}");
    Console.WriteLine($"  Services: {string.Join(", ", candidate.AdvertisedServiceUuids)}");
}

if (currentCandidates.Length == 0)
{
    await discovery.StopScanningAsync();
    Console.WriteLine("No Hue / Signify candidate was seen. No connection attempted.");
    return 3;
}

var selected = requestedDevice is null
    ? currentCandidates.Length == 1 ? currentCandidates[0] : null
    : currentCandidates.FirstOrDefault(candidate =>
        string.Equals(NormalizeAddress(candidate.DeviceKey), NormalizeAddress(requestedDevice), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(NormalizeAddress(candidate.Address), NormalizeAddress(requestedDevice), StringComparison.OrdinalIgnoreCase));
if (selected is null && requestedDevice is not null)
{
    await discovery.StopScanningAsync();
    Console.WriteLine($"No candidate matched --device '{requestedDevice}'. Use one of the addresses printed above.");
    return 4;
}

if (selected is null && !Console.IsInputRedirected)
{
    Console.WriteLine("Choose the Hue candidate to inspect:");
    for (var index = 0; index < currentCandidates.Length; index++)
    {
        Console.WriteLine($"  {index + 1}. {currentCandidates[index].Address} ({currentCandidates[index].AddressType}) · {currentCandidates[index].Name}");
    }

    Console.Write("Candidate number: ");
    var input = Console.ReadLine();
    if (int.TryParse(input, out var selection) && selection >= 1 && selection <= currentCandidates.Length)
    {
        selected = currentCandidates[selection - 1];
    }
}

if (selected is null)
{
    await discovery.StopScanningAsync();
    Console.WriteLine("More than one candidate appeared. Pass --device <BLE address> or run the probe interactively to choose one.");
    return 4;
}

int selectedSignatureCount;
lock (candidateLock) selectedSignatureCount = hueSignatureSamples.GetValueOrDefault(selected.DeviceKey);
Console.WriteLine($"Hue-specific 0xFE0F advertisements observed: {selectedSignatureCount}/3.");
if (selectedSignatureCount < 3)
{
    await discovery.StopScanningAsync();
    Console.WriteLine("Stable Hue service signature was not seen three times. No connection attempted.");
    return 6;
}

Console.WriteLine("Waiting the production flow's 3-second debounce before connection...");
await Task.Delay(TimeSpan.FromSeconds(3));
lock (candidateLock)
    selected = candidates.GetValueOrDefault(selected.DeviceKey) ?? selected;

Console.WriteLine($"Connecting for GATT inspection (no light commands) to {selected.Address} ({selected.AddressType})...");
var advertisementAge = DateTimeOffset.UtcNow - selected.LastSeenUtc;
Console.WriteLine($"Fresh Hue advertisement age at connection attempt: {Math.Max(0, advertisementAge.TotalMilliseconds):F0} ms.");
// Keep the Hue advertisement watcher active while Windows resolves the live
// address. The production WPF flow does this too, then stops scanning after a
// BluetoothLEDevice/GATT session has been created.
IBleConnection connection;
try
{
    connection = await transport.ConnectAsync(selected);
}
catch (Exception exception)
{
    Console.WriteLine($"Connection failed: {exception}");
    await discovery.StopScanningAsync();
    // Gather slower Windows catalog diagnostics only after the live connection
    // attempt, so they cannot invalidate the primary experiment.
    await InspectExactAddressSelectorAsync(selected);
    await InspectWindowsEndpointAsync(selected.Address);
    return 5;
}
await discovery.StopScanningAsync();
await using var connectionLifetime = connection;
var snapshot = await connection.DiscoverAsync();
Console.WriteLine($"Connection state: {snapshot.ConnectionState}; services={snapshot.Services.Count}; discovery errors={snapshot.Errors.Count}");
foreach (var error in snapshot.Errors) Console.WriteLine($"DISCOVERY ERROR: {error}");

foreach (var service in snapshot.Services)
{
    Console.WriteLine($"SERVICE {service.Name} {service.Uuid} status={service.DiscoveryStatus} handle={service.AttributeHandle}");
    foreach (var characteristic in service.Characteristics)
    {
        Console.WriteLine($"  CHARACTERISTIC {characteristic.Name} {characteristic.Key.CharacteristicUuid} properties={characteristic.Operations} read={characteristic.ReadStatus} length={characteristic.ValueByteLength?.ToString() ?? "?"} value={characteristic.ValueHex ?? "<none>"}");
        if (characteristic.Error is not null) Console.WriteLine($"    ERROR: {characteristic.Error}");
        foreach (var descriptor in characteristic.Descriptors)
            Console.WriteLine($"    DESCRIPTOR {descriptor.Name} {descriptor.Uuid} read={descriptor.ReadStatus} value={descriptor.ValueHex ?? "<none>"}");
    }
}

Console.WriteLine("This probe only enumerated GATT and read available values. No characteristic writes or subscriptions were requested.");
return 0;

static BleDeviceInfo MergeAdvertisements(BleDeviceInfo previous, BleDeviceInfo current)
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
            ? "Hue adlarında Signify servis imzası görüldü"
            : current.HueMatchReason ?? previous.HueMatchReason
    };
}

static string NormalizeAddress(string address) =>
    new(address.Where(Uri.IsHexDigit).ToArray());

static async Task InspectWindowsEndpointAsync(string targetAddress)
{
    const string deviceAddressProperty = "System.Devices.Aep.DeviceAddress";
    var selector = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(false);
    Console.WriteLine($"Unpaired LE selector: {selector}");
    foreach (Windows.Devices.Enumeration.DeviceInformationKind? kind in new Windows.Devices.Enumeration.DeviceInformationKind?[]
             { null, Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint })
    {
        var result = new TaskCompletionSource<Windows.Devices.Enumeration.DeviceInformation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var matchedEvents = 0;
        var addedEvents = 0;
        var watcher = kind is null
            ? Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(selector, new[] { deviceAddressProperty })
            : Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(selector, new[] { deviceAddressProperty }, kind.Value);
        watcher.Added += (_, info) =>
        {
            addedEvents++;
            var address = info.Properties.TryGetValue(deviceAddressProperty, out var value) ? value?.ToString() : null;
            if (string.Equals(Normalize(address ?? string.Empty), Normalize(targetAddress), StringComparison.OrdinalIgnoreCase) ||
                info.Name.Contains("Hue", StringComparison.OrdinalIgnoreCase))
            {
                matchedEvents++;
                Console.WriteLine($"Endpoint Added: kind={info.Kind}; name='{info.Name}'; address='{address ?? "<missing>"}'; id='{info.Id}'");
                result.TrySetResult(info);
            }
        };
        watcher.Updated += (_, _) => { };
        watcher.EnumerationCompleted += (_, _) => Console.WriteLine($"Endpoint enumeration completed ({kind?.ToString() ?? "default kind"}); Added={addedEvents}");
        watcher.Stopped += (_, _) => Console.WriteLine($"Endpoint watcher stopped ({kind?.ToString() ?? "default kind"})");
        watcher.Start();
        var found = await Task.WhenAny(result.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        watcher.Stop();
        Console.WriteLine($"Endpoint watcher result ({kind?.ToString() ?? "default kind"}): status={watcher.Status}; Added={addedEvents}; Hue/target={matchedEvents}");
        if (found == result.Task)
        {
            var device = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(result.Task.Result.Id);
            Console.WriteLine($"FromIdAsync result: {(device is null ? "null" : $"{device.Name}; paired={device.DeviceInformation.Pairing.IsPaired}")}");
            device?.Dispose();
        }
    }

    static string Normalize(string address) => new(address.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
}

static async Task InspectExactAddressSelectorAsync(BleDeviceInfo target)
{
    var addressType = string.Equals(target.AddressType, "Random", StringComparison.OrdinalIgnoreCase)
        ? Windows.Devices.Bluetooth.BluetoothAddressType.Random
        : Windows.Devices.Bluetooth.BluetoothAddressType.Public;
    var selector = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(
        target.BluetoothAddress, addressType);
    Console.WriteLine($"Exact address/type selector: {selector}");

    try
    {
        var current = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
            selector,
            new[] { "System.Devices.Aep.DeviceAddress" },
            Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint);
        Console.WriteLine($"Exact address/type FindAllAsync returned {current.Count} endpoint(s).");
        foreach (var deviceInfo in current)
        {
            Console.WriteLine($"Exact selector result: kind={deviceInfo.Kind}; name='{deviceInfo.Name}'; id='{deviceInfo.Id}'");
            try
            {
                var device = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
                Console.WriteLine(device is null
                    ? "FindAll FromIdAsync result: null"
                    : $"FindAll FromIdAsync result: {device.Name}; address={device.BluetoothAddress:X12}; type={device.BluetoothAddressType}; paired={device.DeviceInformation.Pairing.IsPaired}");
                device?.Dispose();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"FindAll FromIdAsync failed: {exception.GetType().Name}; HRESULT 0x{unchecked((uint)exception.HResult):X8}; {exception.Message}");
            }
        }
    }
    catch (Exception exception)
    {
        Console.WriteLine($"Exact address/type FindAllAsync failed: {exception.GetType().Name}; HRESULT 0x{unchecked((uint)exception.HResult):X8}; {exception.Message}");
    }

    var found = new TaskCompletionSource<Windows.Devices.Enumeration.DeviceInformation?>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var watcher = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(
        selector,
        new[] { "System.Devices.Aep.DeviceAddress" },
        Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint);
    var addedCount = 0;
    watcher.Added += (_, information) =>
    {
        addedCount++;
        Console.WriteLine($"Exact selector Added: kind={information.Kind}; name='{information.Name}'; id='{information.Id}'");
        found.TrySetResult(information);
    };
    watcher.EnumerationCompleted += (_, _) =>
    {
        Console.WriteLine($"Exact selector enumeration completed; Added={addedCount}");
        found.TrySetResult(null);
    };
    watcher.Stopped += (_, _) => Console.WriteLine($"Exact selector stopped: {watcher.Status}");

    watcher.Start();
    var completed = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(10)));
    watcher.Stop();
    var information = completed == found.Task ? found.Task.Result : null;
    if (information is null)
    {
        Console.WriteLine("Exact address/type selector returned no Windows device endpoint.");
        return;
    }

    try
    {
        var device = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(information.Id);
        Console.WriteLine(device is null
            ? "FromIdAsync result: null"
            : $"FromIdAsync result: {device.Name}; address={device.BluetoothAddress:X12}; type={device.BluetoothAddressType}; paired={device.DeviceInformation.Pairing.IsPaired}");
        device?.Dispose();
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FromIdAsync failed: {exception.GetType().Name}; HRESULT 0x{unchecked((uint)exception.HResult):X8}; {exception.Message}");
    }
}
