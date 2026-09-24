using HuePC.Core.Models;

namespace HuePC.Core.Interfaces;

public sealed class DiscoveredDeviceEventArgs(BleDeviceInfo device) : EventArgs
{
    public BleDeviceInfo Device { get; } = device;
}

public interface IDeviceDiscovery : IAsyncDisposable
{
    event EventHandler<DiscoveredDeviceEventArgs>? DeviceDiscovered;
    event EventHandler<string>? ScanError;
    Task<AdapterStatus> GetAdapterStatusAsync(CancellationToken cancellationToken = default);
    Task StartScanningAsync(CancellationToken cancellationToken = default);
    Task StopScanningAsync(CancellationToken cancellationToken = default);
}
