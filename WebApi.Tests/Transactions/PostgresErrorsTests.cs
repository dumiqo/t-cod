using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Transactions;

[TestClass]
public class PostgresErrorsTests
{
    [TestMethod]
    public async Task Query_LockTimeout_WrappedInInvalidOperationException()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await context.Classes.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        await using var held = await HeldLock.AcquireAsync(db.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR UPDATE");
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '100ms'");

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            context.Classes.FromSql($"SELECT * FROM classes WHERE id = {classId} FOR NO KEY UPDATE").ToListAsync());
        var postgres = (PostgresException)exception.InnerException!;

        Assert.AreEqual(PostgresErrorCodes.LockNotAvailable, postgres.SqlState);
    }

    [TestMethod]
    public async Task SaveChanges_LockTimeout_WrappedInDbUpdateExceptionInsideInvalidOperationException()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var pass = await context.Passes.OrderBy(x => x.Id).FirstAsync();
        await using var held = await HeldLock.AcquireAsync(db.ConnectionString, $"SELECT 1 FROM passes WHERE id = {pass.Id} FOR UPDATE");
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '100ms'");

        pass.RemainingVisits += 1;

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        var update = (DbUpdateException)exception.InnerException!;
        var postgres = (PostgresException)update.InnerException!;

        Assert.AreEqual(PostgresErrorCodes.LockNotAvailable, postgres.SqlState);
    }
}
