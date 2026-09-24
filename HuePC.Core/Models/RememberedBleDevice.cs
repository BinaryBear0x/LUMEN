namespace HuePC.Core.Models;

public sealed record RememberedBleDevice(BleDeviceInfo Device, DateTimeOffset RememberedAtUtc);
