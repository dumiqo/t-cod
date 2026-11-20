using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public interface IWaitlistRepository : IGenericRepository<WaitlistEntry>
{
    Task<int> GetPositionAsync(long classId, long id, CancellationToken ct = default);

    Task<bool> IsWaitingAsync(long classId, long userId, CancellationToken ct = default);

    Task<WaitlistEntry?> FindOwnedAsync(long id, long userId, CancellationToken ct = default);

    Task<List<WaitlistEntry>> GetWaitingAsync(long classId, CancellationToken ct = default);
}
