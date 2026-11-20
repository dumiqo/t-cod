using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IClassRepository : IGenericRepository<Class>
{
    Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default);

    Task<Class?> LockIncludingCancelledAsync(long id, CancellationToken ct = default);

    Task<List<ScheduleRow>> GetSchedulePageAsync(ScheduleQuery query, long userId, CancellationToken ct = default);
}
