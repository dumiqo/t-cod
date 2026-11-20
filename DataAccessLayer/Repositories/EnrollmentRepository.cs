using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DataAccessLayer.Repositories;

public class EnrollmentRepository : GenericRepository<Enrollment>, IEnrollmentRepository
{
    public EnrollmentRepository(DbSet<Enrollment> dbSet)
        : base(dbSet)
    {
    }

    public async Task<List<Enrollment>> GetByUserAsync(long userId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<long> SumActiveSeatsAsync(long classId, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(x => x.ClassId == classId && x.Status == EnrollmentStatuses.Active)
            .SumAsync(x => (long)x.Seats, ct);
    }

    public async Task<Enrollment?> FindOwnedAsync(long id, long userId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
    }

    public async Task<EnrollmentCard?> GetCardAsync(long id, long userId, CancellationToken ct = default)
    {
        return (await GetVersionedCardAsync(id, userId, ct))?.Card;
    }

    public async Task<VersionedCard?> GetVersionedCardAsync(long id, long userId, CancellationToken ct = default)
    {
        var passes = _dbSet.GetService<ICurrentDbContext>().Context.Set<Pass>();

        // решение: бронь, занятие, зал и абонемент — одним SELECT: отдельные запросы при параллельной
        // отмене смешали бы бронь до отмены с абонементом после (задача 4).
        return await _dbSet
            .IgnoreQueryFilters()
            .Where(x => x.Id == id && x.UserId == userId)
            .Join(passes, x => x.UserId, x => x.UserId, (x, pass) => new VersionedCard(new EnrollmentCard(
                x.Id,
                x.Status,
                x.Seats,
                x.CreatedAt,
                new EnrollmentCardClass(
                    x.Class.Id,
                    x.Class.Title,
                    x.Class.StartsAt,
                    x.Class.DurationMinutes,
                    x.Class.IsCancelled,
                    new EnrollmentCardRoom(x.Class.Room.Id, x.Class.Room.Name)),
                new EnrollmentCardPass(pass.RemainingVisits, pass.ValidUntil)), pass.Version))
            .SingleOrDefaultAsync(ct);
    }
}
