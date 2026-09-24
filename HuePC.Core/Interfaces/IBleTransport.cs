using HuePC.Core.Models;
using HuePC.Core.Services;

namespace HuePC.Core.Interfaces;

public sealed class ConnectionStateChangedEventArgs(string state, string? error = null) : EventArgs
{
    public string State { get; } = state;
    public string? Error { get; } = error;
}

public sealed class GattNotificationEventArgs(GattNotificationEvent notification) : EventArgs
{
    public GattNotificationEvent Notification { get; } = notification;
}

public sealed class LightStateChangedEventArgs(HueLightState state) : EventArgs
{
    public HueLightState State { get; } = state;
}

public interface IBleTransport
{
    Task<IBleConnection> ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default);
}

public interface IBleConnection : IAsyncDisposable
{
    BleDeviceInfo Device { get; }
    string ConnectionState { get; }
    bool IsPaired { get; }
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    event EventHandler<GattNotificationEventArgs>? NotificationReceived;
    event EventHandler<LightStateChangedEventArgs>? LightStateChanged;
    Task<GattDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<GattOperationResult> SetNotificationsAsync(GattAttributeKey key, bool enabled, CancellationToken cancellationToken = default);
    Task<GattOperationResult> SetPowerAsync(bool on, CancellationToken cancellationToken = default);
    Task<GattOperationResult> SetBrightnessAsync(byte brightness, CancellationToken cancellationToken = default);
    Task<GattOperationResult> SetColorTemperatureAsync(ushort mired, CancellationToken cancellationToken = default);
    Task<GattOperationResult> SetColorAsync(ushort x, ushort y, CancellationToken cancellationToken = default);
    Task<GattOperationResult> SetColorAndBrightnessAsync(ushort x, ushort y, byte brightness, CancellationToken cancellationToken = default);
    Task<GattOperationResult> SetEffectAsync(HueEffect effect, byte speed, CancellationToken cancellationToken = default);
    Task<GattOperationResult> SetEffectSpeedAsync(byte speed, CancellationToken cancellationToken = default);
    Task<GattOperationResult> IdentifyAsync(CancellationToken cancellationToken = default);
    Task<GattOperationResult> RefreshLightStateAsync(CancellationToken cancellationToken = default);
    Task<HueDeviceInfo?> ReadDeviceInfoAsync(CancellationToken cancellationToken = default);
    Task<GattOperationResult> SetDeviceNameAsync(string name, CancellationToken cancellationToken = default);
    Task<HuePowerOnBehaviour?> ReadPowerOnBehaviourAsync(CancellationToken cancellationToken = default);
    Task<GattOperationResult> SetPowerOnBehaviourAsync(HuePowerOnBehaviour behaviour, CancellationToken cancellationToken = default);
}
