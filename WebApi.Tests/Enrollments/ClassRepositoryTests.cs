using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class ClassRepositoryTests
{
    [TestMethod]
    public async Task LockIncludingCancelledAsync_CancelledClass_ReturnsIt()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var cancelledId = await CancelledClassIdAsync(context);

        var found = await new UnitOfWork(context).Class.LockIncludingCancelledAsync(cancelledId);

        Assert.IsNotNull(found);
        Assert.AreEqual(cancelledId, found.Id);
        Assert.IsTrue(found.IsCancelled);
    }

    [TestMethod]
    public async Task LockIncludingCancelledAsync_ActiveClass_ReturnsIt()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var active = await context.Classes.AsNoTracking().OrderBy(x => x.Id).FirstAsync();

        var found = await new UnitOfWork(context).Class.LockIncludingCancelledAsync(active.Id);

        Assert.IsNotNull(found);
        Assert.AreEqual(active.Id, found.Id);
        Assert.AreEqual(active.StartsAt, found.StartsAt);
        Assert.AreEqual(active.Capacity, found.Capacity);
        Assert.IsFalse(found.IsCancelled);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(999999999L)]
    public async Task LockIncludingCancelledAsync_MissingClass_ReturnsNull(long id)
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();

        var found = await new UnitOfWork(context).Class.LockIncludingCancelledAsync(id);

        Assert.IsNull(found);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LockIncludingCancelledAsync_InTransaction_OtherSessionCannotLockClass(bool cancelled)
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = cancelled
            ? await CancelledClassIdAsync(context)
            : await context.Classes.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        await new UnitOfWork(context).Class.LockIncludingCancelledAsync(classId);

        var exception = await Assert.ThrowsExceptionAsync<PostgresException>(() =>
            HeldLock.AcquireAsync(db.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE NOWAIT"));
        Assert.AreEqual("55P03", exception.SqlState);
    }

    [TestMethod]
    public async Task LockIncludingCancelledAsync_InTransaction_OtherSessionInsertsEnrollmentWithoutWaiting()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await context.Classes.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        var userId = await context.Users.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await new UnitOfWork(context).Class.LockIncludingCancelledAsync(classId);
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SET lock_timeout = '300ms';
            INSERT INTO enrollments (user_id, class_id, seats, status, created_at)
            VALUES ({userId}, {classId}, 1, 'active', now());
            """,
            connection);

        var inserted = await command.ExecuteNonQueryAsync();

        Assert.AreEqual(1, inserted);
    }

    private static Task<long> CancelledClassIdAsync(DataAccessLayer.ClubDbContext context)
    {
        return context.Classes.IgnoreQueryFilters()
            .Where(x => x.IsCancelled)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .FirstAsync();
    }
}
