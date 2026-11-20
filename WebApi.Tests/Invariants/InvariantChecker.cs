using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests.Invariants;

public static class InvariantChecker
{
    public static async Task<Dictionary<long, int>> BalancesAsync(string connectionString, IReadOnlyCollection<long> classIds, IReadOnlyCollection<long> userIds)
    {
        await using var context = CreateContext(connectionString);
        var remaining = await context.Passes.AsNoTracking()
            .Where(x => userIds.Contains(x.UserId))
            .ToDictionaryAsync(x => x.UserId, x => x.RemainingVisits);
        var charged = await context.Enrollments.AsNoTracking()
            .Where(x => classIds.Contains(x.ClassId) && userIds.Contains(x.UserId))
            .GroupBy(x => x.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Visits = g.Sum(x => x.Status == EnrollmentStatuses.Active ? x.Seats : x.Seats - (x.RefundedVisits ?? 0))
            })
            .ToDictionaryAsync(x => x.UserId, x => x.Visits);

        return remaining.ToDictionary(x => x.Key, x => x.Value + charged.GetValueOrDefault(x.Key));
    }

    public static async Task<List<string>> CheckAsync(string connectionString, IReadOnlyCollection<long> classIds, IReadOnlyDictionary<long, int> initialBalances)
    {
        var violations = new List<string>();
        await using var context = CreateContext(connectionString);

        var classes = await context.Classes.IgnoreQueryFilters().AsNoTracking()
            .Where(x => classIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                x.Capacity,
                Occupied = context.Enrollments.Where(e => e.ClassId == x.Id && e.Status == EnrollmentStatuses.Active).Sum(e => e.Seats)
            })
            .ToListAsync();
        violations.AddRange(classes.Where(x => x.Occupied > x.Capacity).Select(x => $"class {x.Id}: {x.Occupied} active seats > capacity {x.Capacity}"));

        var balances = await BalancesAsync(connectionString, classIds, initialBalances.Keys.ToList());
        foreach (var (userId, initial) in initialBalances)
        {
            if (balances[userId] != initial)
            {
                violations.Add($"user {userId}: remaining + charged = {balances[userId]}, initial {initial}");
            }
        }

        var negative = await context.Passes.AsNoTracking().Where(x => x.RemainingVisits < 0).Select(x => x.UserId).ToListAsync();
        violations.AddRange(negative.Select(x => $"user {x}: negative remaining visits"));

        var duplicates = await context.WaitlistEntries.AsNoTracking()
            .Where(x => classIds.Contains(x.ClassId) && x.Status == WaitlistStatuses.Waiting)
            .GroupBy(x => new { x.ClassId, x.UserId })
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync();
        violations.AddRange(duplicates.Select(x => $"class {x.ClassId}: user {x.UserId} waits more than once"));

        return violations;
    }

    private static ClubDbContext CreateContext(string connectionString)
    {
        return new ClubDbContext(new DbContextOptionsBuilder<ClubDbContext>().UseNpgsql(connectionString).Options);
    }
}
