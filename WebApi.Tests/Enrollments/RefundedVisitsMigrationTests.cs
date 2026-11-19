using DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class RefundedVisitsMigrationTests
{
    private const string BeforeRefundedVisits = "20261119170046_AddIdempotencyKeys";

    [TestMethod]
    public async Task Seed_CancelledEnrollments_RefundedVisitsEqualSeats()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();

        await AssertSeededRefundsAsync(context);
    }

    [TestMethod]
    public async Task Migrate_ExistingSeededDatabase_FillsRefundedVisitsOfCancelledWithSeats()
    {
        await using var db = await TestDb.CloneAsync();
        await using (var down = db.CreateContext())
        {
            await down.GetService<IMigrator>().MigrateAsync(BeforeRefundedVisits);
            Assert.AreEqual(BeforeRefundedVisits, (await down.Database.GetAppliedMigrationsAsync()).Last());
        }

        await using (var up = db.CreateContext())
        {
            await up.Database.MigrateAsync();
            Assert.AreEqual(0, (await up.Database.GetPendingMigrationsAsync()).Count());
        }

        await using var context = db.CreateContext();
        await AssertSeededRefundsAsync(context);
    }

    private static async Task AssertSeededRefundsAsync(ClubDbContext context)
    {
        var enrollments = await context.Enrollments.AsNoTracking().ToListAsync();
        var cancelled = enrollments.Where(x => x.Status == "cancelled").ToList();
        var active = enrollments.Where(x => x.Status == "active").ToList();

        Assert.AreEqual(50, cancelled.Count);
        Assert.AreEqual(enrollments.Count, cancelled.Count + active.Count);
        Assert.IsTrue(active.Count > 0);
        foreach (var enrollment in cancelled)
        {
            Assert.AreEqual(enrollment.Seats, enrollment.RefundedVisits, $"enrollment {enrollment.Id}");
        }

        foreach (var enrollment in active)
        {
            Assert.IsNull(enrollment.RefundedVisits, $"enrollment {enrollment.Id}");
        }
    }
}
