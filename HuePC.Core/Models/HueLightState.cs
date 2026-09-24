namespace HuePC.Core.Models;

/// <summary>
/// Light state reported by the bulb. Every field is optional because notifications and reads may
/// carry only part of the state.
/// </summary>
public sealed record HueLightState(
    bool? IsOn,
    byte? Brightness,
    ushort? ColorTemperatureMired,
    ushort? ColorX,
    ushort? ColorY,
    byte? Effect = null,
    byte? EffectSpeed = null)
{
    public bool IsColorTemperatureMode => ColorTemperatureMired is not null && ColorTemperatureMired != 0xFFFF;

    public bool IsColorMode => ColorX is not null && ColorY is not null && (ColorX.Value | ColorY.Value) != 0;
}
