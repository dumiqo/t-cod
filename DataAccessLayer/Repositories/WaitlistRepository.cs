using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class WaitlistRepository : GenericRepository<WaitlistEntry>, IWaitlistRepository
{
    public WaitlistRepository(DbSet<WaitlistEntry> dbSet)
        : base(dbSet)
    {
    }

    public async Task<int> GetPositionAsync(long classId, long id, CancellationToken ct = default)
    {
        // решение: порядок очереди — по id (задача 19): id выдаётся при вставке под замком занятия, время может совпасть или пойти назад.
        return await _dbSet.CountAsync(x => x.ClassId == classId && x.Status == WaitlistStatuses.Waiting && x.Id <= id, ct);
    }

    public async Task<bool> IsWaitingAsync(long classId, long userId, CancellationToken ct = default)
    {
        return await _dbSet.AnyAsync(x => x.ClassId == classId && x.UserId == userId && x.Status == WaitlistStatuses.Waiting, ct);
    }

    public async Task<WaitlistEntry?> FindOwnedAsync(long id, long userId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
    }
}
