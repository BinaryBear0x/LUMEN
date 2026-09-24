using HuePC.Core.Models;

namespace HuePC.Core.Interfaces;

public interface IScheduleStore
{
    Task<IReadOnlyList<LightSchedule>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<LightSchedule> schedules, CancellationToken cancellationToken = default);
}
