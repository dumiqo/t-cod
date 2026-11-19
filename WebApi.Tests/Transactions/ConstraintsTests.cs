using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Transactions;

[TestClass]
public class ConstraintsTests
{
    [TestMethod]
    public async Task SaveChanges_NegativeRemainingVisits_ViolatesCheckConstraint()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var pass = await context.Passes.OrderBy(x => x.Id).FirstAsync();

        pass.RemainingVisits = -1;

        var exception = await Assert.ThrowsExceptionAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var postgres = (PostgresException)exception.InnerException!;

        Assert.AreEqual(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.AreEqual("ck_passes_remaining_visits", postgres.ConstraintName);
    }

    [TestMethod]
    public async Task SaveChanges_ZeroRemainingVisits_Saves()
    {
        await using var db = await TestDb.CloneAsync();
        long passId;

        await using (var context = db.CreateContext())
        {
            var pass = await context.Passes.OrderBy(x => x.Id).FirstAsync();
            passId = pass.Id;
            pass.RemainingVisits = 0;
            await context.SaveChangesAsync();
        }

        await using var check = db.CreateContext();

        Assert.AreEqual(0, await check.Passes.Where(x => x.Id == passId).Select(x => x.RemainingVisits).SingleAsync());
    }

    [TestMethod]
    public async Task SaveChanges_ZeroSeats_ViolatesCheckConstraint()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var enrollment = await context.Enrollments.IgnoreQueryFilters().OrderBy(x => x.Id).FirstAsync();

        enrollment.Seats = 0;

        var exception = await Assert.ThrowsExceptionAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var postgres = (PostgresException)exception.InnerException!;

        Assert.AreEqual(PostgresErrorCodes.CheckViolation, postgres.SqlState);
        Assert.AreEqual("ck_enrollments_seats", postgres.ConstraintName);
    }

    [TestMethod]
    public async Task SaveChanges_SecondPassForUser_ViolatesUniqueIndex()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var pass = await context.Passes.OrderBy(x => x.Id).FirstAsync();

        context.Passes.Add(new Pass { UserId = pass.UserId, RemainingVisits = 5, ValidUntil = pass.ValidUntil });

        var exception = await Assert.ThrowsExceptionAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var postgres = (PostgresException)exception.InnerException!;

        Assert.AreEqual(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.AreEqual("IX_passes_user_id", postgres.ConstraintName);
    }
}
