using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class ClassRepository : GenericRepository<Class>, IClassRepository
{
    public ClassRepository(DbSet<Class> dbSet)
        : base(dbSet)
    {
    }

    public async Task<Class?> GetWithEnrollmentsAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .Include(x => x.Enrollments)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<Class?> LockIncludingCancelledAsync(long id, CancellationToken ct = default)
    {
        return await _dbSet
            .FromSql($"SELECT * FROM classes WHERE id = {id} FOR NO KEY UPDATE")
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(ct);
    }

    public async Task<List<ScheduleRow>> GetSchedulePageAsync(ScheduleQuery query, CancellationToken ct = default)
    {
        var classes = _dbSet.Where(x => x.StartsAt >= query.From && x.StartsAt < query.To);

        if (query.RoomId is { } roomId)
        {
            classes = classes.Where(x => x.RoomId == roomId);
        }

        return await classes
            .OrderBy(x => x.StartsAt)
            // решение: id — второй ключ: контракт не задаёт порядок внутри одного startsAt, а курсору нужен однозначный.
            .ThenBy(x => x.Id)
            .Take(query.Take)
            .Select(x => new ScheduleRow(x.Id, x.Title, x.StartsAt, x.DurationMinutes, x.Capacity, x.RoomId, x.Room.Name))
            .ToListAsync(ct);
    }
}
