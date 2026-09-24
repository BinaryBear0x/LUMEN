namespace HuePC.Core.Models;

[Flags]
public enum ScheduleDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    EveryDay = Weekdays | Weekend
}

public enum ScheduleAction
{
    TurnOnWithState,
    TurnOff
}

/// <summary>
/// One alarm entry. When the action is <see cref="ScheduleAction.TurnOnWithState"/> the captured
/// brightness/colour values are applied; otherwise the bulb is switched off.
/// </summary>
public sealed record LightSchedule(
    Guid Id,
    string Name,
    TimeOnly Time,
    ScheduleDays Days,
    ScheduleAction Action,
    byte Brightness,
    bool UseColor,
    ushort ColorTemperatureMired,
    ushort ColorX,
    ushort ColorY,
    bool Enabled,
    int WakeUpRampMinutes = 0)
{
    public static LightSchedule CreateDefault(DateTimeOffset now) => new(
        Guid.NewGuid(),
        "Yeni alarm",
        new TimeOnly(now.Hour, now.Minute),
        ScheduleDays.EveryDay,
        ScheduleAction.TurnOnWithState,
        254,
        false,
        300,
        0,
        0,
        true,
        0);
}
