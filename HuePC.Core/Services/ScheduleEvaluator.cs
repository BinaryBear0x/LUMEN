using HuePC.Core.Models;

namespace HuePC.Core.Services;

/// <summary>
/// Decides whether a schedule is due. A schedule fires once per day inside a short window after
/// its time so a briefly busy or restarted app does not miss it.
/// </summary>
public static class ScheduleEvaluator
{
    public static readonly TimeSpan DueWindow = TimeSpan.FromSeconds(90);

    public static bool IsDue(LightSchedule schedule, DateTimeOffset now, DateTimeOffset? lastRun)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!schedule.Enabled)
        {
            return false;
        }

        var local = now.ToLocalTime().DateTime;
        if (!MatchesDay(schedule.Days, local.DayOfWeek))
        {
            return false;
        }

        var scheduled = local.Date + schedule.Time.ToTimeSpan();
        var delta = local - scheduled;
        if (delta < TimeSpan.Zero || delta >= DueWindow)
        {
            return false;
        }

        return lastRun is null || lastRun.Value.ToLocalTime().Date != local.Date;
    }

    /// <summary>
    /// True when the wake-up ramp of a schedule should start now: the ramp begins
    /// <see cref="LightSchedule.WakeUpRampMinutes"/> before the alarm time and runs once per day.
    /// </summary>
    public static bool IsRampDue(LightSchedule schedule, DateTimeOffset now, DateTimeOffset? lastRun)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!schedule.Enabled || schedule.WakeUpRampMinutes <= 0)
        {
            return false;
        }

        var local = now.ToLocalTime().DateTime;
        if (!MatchesDay(schedule.Days, local.DayOfWeek))
        {
            return false;
        }

        var rampStart = local.Date + schedule.Time.ToTimeSpan() - TimeSpan.FromMinutes(schedule.WakeUpRampMinutes);
        var delta = local - rampStart;
        if (delta < TimeSpan.Zero || delta >= DueWindow)
        {
            return false;
        }

        return lastRun is null || lastRun.Value.ToLocalTime().Date != local.Date;
    }

    public static bool MatchesDay(ScheduleDays days, DayOfWeek day) => (days & Flag(day)) != 0;

    public static string DescribeDays(ScheduleDays days)
    {
        if (days == ScheduleDays.EveryDay) return "Her gün";
        if (days == ScheduleDays.Weekdays) return "Hafta içi";
        if (days == ScheduleDays.Weekend) return "Hafta sonu";
        if (days == ScheduleDays.None) return "Gün seçilmedi";

        var names = new List<string>();
        foreach (var (flag, name) in DayNames)
        {
            if ((days & flag) != 0) names.Add(name);
        }

        return string.Join(", ", names);
    }

    private static readonly (ScheduleDays Flag, string Name)[] DayNames =
    [
        (ScheduleDays.Monday, "Pzt"),
        (ScheduleDays.Tuesday, "Sal"),
        (ScheduleDays.Wednesday, "Çar"),
        (ScheduleDays.Thursday, "Per"),
        (ScheduleDays.Friday, "Cum"),
        (ScheduleDays.Saturday, "Cmt"),
        (ScheduleDays.Sunday, "Paz")
    ];

    private static ScheduleDays Flag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => ScheduleDays.Monday,
        DayOfWeek.Tuesday => ScheduleDays.Tuesday,
        DayOfWeek.Wednesday => ScheduleDays.Wednesday,
        DayOfWeek.Thursday => ScheduleDays.Thursday,
        DayOfWeek.Friday => ScheduleDays.Friday,
        DayOfWeek.Saturday => ScheduleDays.Saturday,
        DayOfWeek.Sunday => ScheduleDays.Sunday,
        _ => ScheduleDays.None
    };
}
