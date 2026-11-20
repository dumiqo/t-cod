using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class PassRepositoryTests
{
    [TestMethod]
    public async Task LockByUserAsync_ExistingUser_ReturnsHisPass()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var expected = await context.Passes.AsNoTracking().OrderByDescending(x => x.Id).FirstAsync();

        var found = await new UnitOfWork(context).Pass.LockByUserAsync(expected.UserId);

        Assert.IsNotNull(found);
        Assert.AreEqual(expected.Id, found.Id);
        Assert.AreEqual(expected.UserId, found.UserId);
        Assert.AreEqual(expected.RemainingVisits, found.RemainingVisits);
        Assert.AreEqual(expected.ValidUntil, found.ValidUntil);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(999999999L)]
    public async Task LockByUserAsync_MissingUser_ReturnsNull(long userId)
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();

        var found = await new UnitOfWork(context).Pass.LockByUserAsync(userId);

        Assert.IsNull(found);
    }

    [TestMethod]
    public async Task LockByUserAsync_InTransaction_OtherSessionCannotEvenKeyShareLockPass()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var pass = await context.Passes.AsNoTracking().OrderBy(x => x.Id).FirstAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        await new UnitOfWork(context).Pass.LockByUserAsync(pass.UserId);

        var exception = await Assert.ThrowsExceptionAsync<PostgresException>(() =>
            HeldLock.AcquireAsync(db.ConnectionString, $"SELECT 1 FROM passes WHERE id = {pass.Id} FOR KEY SHARE NOWAIT"));
        Assert.AreEqual("55P03", exception.SqlState);
    }

    [TestMethod]
    public async Task LockByUsersAsync_SomeUsers_ReturnsTheirPassesOrderedById()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var passes = await context.Passes.AsNoTracking().OrderBy(x => x.Id).Take(3).ToListAsync();

        var found = await new UnitOfWork(context).Pass.LockByUsersAsync([passes[2].UserId, 999999999L, passes[0].UserId, passes[2].UserId]);

        CollectionAssert.AreEqual(
            new[] { $"{passes[0].Id}|{passes[0].UserId}", $"{passes[2].Id}|{passes[2].UserId}" },
            found.Select(x => $"{x.Id}|{x.UserId}").ToArray());
    }

    [TestMethod]
    public async Task LockByUsersAsync_InTransaction_OtherSessionCannotEvenKeyShareLockAnyOfPasses()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var passes = await context.Passes.AsNoTracking().OrderBy(x => x.Id).Take(2).ToListAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        await new UnitOfWork(context).Pass.LockByUsersAsync([passes[0].UserId, passes[1].UserId]);

        foreach (var pass in passes)
        {
            var exception = await Assert.ThrowsExceptionAsync<PostgresException>(() =>
                HeldLock.AcquireAsync(db.ConnectionString, $"SELECT 1 FROM passes WHERE id = {pass.Id} FOR KEY SHARE NOWAIT"));
            Assert.AreEqual("55P03", exception.SqlState);
        }
    }
}
