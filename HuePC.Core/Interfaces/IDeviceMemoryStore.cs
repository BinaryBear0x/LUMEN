using HuePC.Core.Models;

namespace HuePC.Core.Interfaces;

public interface IDeviceMemoryStore
{
    Task<RememberedBleDevice?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(BleDeviceInfo device, CancellationToken cancellationToken = default);
    Task ForgetAsync(CancellationToken cancellationToken = default);
}
