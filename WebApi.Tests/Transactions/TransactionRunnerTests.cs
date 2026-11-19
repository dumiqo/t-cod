using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Transactions;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Transactions;

[TestClass]
public class TransactionRunnerTests
{
    private const string Operation = "probe-operation";

    private static readonly TimeSpan LockTimeout = TimeSpan.FromMilliseconds(300);

    [TestMethod]
    public async Task RunAsync_Commit_PersistsChangesAndReturnsValue()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var (runner, unitOfWork) = CreateRunner(context);
        var pass = await context.Passes.OrderBy(x => x.Id).FirstAsync();
        var expected = pass.RemainingVisits + 1;

        var result = await runner.RunAsync(Operation, async ct =>
        {
            var tracked = await unitOfWork.Pass.GetByIdAsync(pass.Id, ct);
            tracked!.RemainingVisits = expected;
            await unitOfWork.SaveChangesAsync(ct);
            return TransactionResult.Commit("committed");
        });

        Assert.AreEqual("committed", result);
        Assert.AreEqual(expected, await RemainingVisitsAsync(db, pass.Id));
    }

    [TestMethod]
    public async Task RunAsync_Rollback_DiscardsChangesAndReturnsValue()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var (runner, unitOfWork) = CreateRunner(context);
        var pass = await context.Passes.AsNoTracking().OrderBy(x => x.Id).FirstAsync();

        var result = await runner.RunAsync(Operation, async ct =>
        {
            var tracked = await unitOfWork.Pass.GetByIdAsync(pass.Id, ct);
            tracked!.RemainingVisits += 1;
            await unitOfWork.SaveChangesAsync(ct);
            return TransactionResult.Rollback("rejected");
        });

        Assert.AreEqual("rejected", result);
        Assert.AreEqual(pass.RemainingVisits, await RemainingVisitsAsync(db, pass.Id));
    }

    [TestMethod]
    public async Task RunAsync_DelegateThrows_RollsBackAndRethrows()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var (runner, unitOfWork) = CreateRunner(context);
        var pass = await context.Passes.AsNoTracking().OrderBy(x => x.Id).FirstAsync();
        var thrown = new ApplicationException("probe");

        var exception = await Assert.ThrowsExceptionAsync<ApplicationException>(() =>
            runner.RunAsync<string>(Operation, async ct =>
            {
                var tracked = await unitOfWork.Pass.GetByIdAsync(pass.Id, ct);
                tracked!.RemainingVisits += 1;
                await unitOfWork.SaveChangesAsync(ct);
                throw thrown;
            }));

        Assert.AreSame(thrown, exception);
        Assert.AreEqual(pass.RemainingVisits, await RemainingVisitsAsync(db, pass.Id));
    }

    [TestMethod]
    public async Task RunAsync_InsideTransaction_LockTimeoutFromSettingsAndOnlyLocal()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var (runner, _) = CreateRunner(context);
        await context.Database.OpenConnectionAsync();

        var inside = await runner.RunAsync(Operation, async ct =>
            TransactionResult.Commit(await ShowLockTimeoutAsync(context, ct)));
        var after = await ShowLockTimeoutAsync(context, CancellationToken.None);

        Assert.AreEqual("300ms", inside);
        Assert.AreEqual("0", after);
    }

    [TestMethod]
    public async Task RunAsync_LockTimeoutOnQuery_ThrowsConflictAndRollsBack()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var (runner, unitOfWork) = CreateRunner(context);
        var pass = await context.Passes.AsNoTracking().OrderBy(x => x.Id).FirstAsync();
        var classId = await context.Classes.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        await using var held = await HeldLock.AcquireAsync(db.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR UPDATE");

        var exception = await Assert.ThrowsExceptionAsync<TransactionConflictException>(() =>
            runner.RunAsync(Operation, async ct =>
            {
                var tracked = await unitOfWork.Pass.GetByIdAsync(pass.Id, ct);
                tracked!.RemainingVisits += 1;
                await unitOfWork.SaveChangesAsync(ct);
                await context.Classes.FromSql($"SELECT * FROM classes WHERE id = {classId} FOR NO KEY UPDATE").ToListAsync(ct);
                return TransactionResult.Commit(0);
            }));

        Assert.AreEqual(Operation, exception.Operation);
        Assert.AreEqual("lock-timeout", exception.Reason);
        Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
        Assert.AreEqual(pass.RemainingVisits, await RemainingVisitsAsync(db, pass.Id));
    }

    [TestMethod]
    public async Task RunAsync_LockTimeoutOnSaveChanges_ThrowsConflictAndRollsBack()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var (runner, unitOfWork) = CreateRunner(context);
        var passes = await context.Passes.AsNoTracking().OrderBy(x => x.Id).Take(2).ToArrayAsync();
        await using var held = await HeldLock.AcquireAsync(db.ConnectionString, $"SELECT 1 FROM passes WHERE id = {passes[1].Id} FOR UPDATE");

        var exception = await Assert.ThrowsExceptionAsync<TransactionConflictException>(() =>
            runner.RunAsync(Operation, async ct =>
            {
                var free = await unitOfWork.Pass.GetByIdAsync(passes[0].Id, ct);
                free!.RemainingVisits += 1;
                await unitOfWork.SaveChangesAsync(ct);
                var locked = await unitOfWork.Pass.GetByIdAsync(passes[1].Id, ct);
                locked!.RemainingVisits += 1;
                await unitOfWork.SaveChangesAsync(ct);
                return TransactionResult.Commit(0);
            }));

        Assert.AreEqual(Operation, exception.Operation);
        Assert.AreEqual("lock-timeout", exception.Reason);
        Assert.AreEqual(passes[0].RemainingVisits, await RemainingVisitsAsync(db, passes[0].Id));
        Assert.AreEqual(passes[1].RemainingVisits, await RemainingVisitsAsync(db, passes[1].Id));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task RunAsync_Deadlock_ThrowsConflictWithDeadlockReasonAndRollsBack()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var (runner, unitOfWork) = CreateRunner(context, TimeSpan.FromSeconds(2));
        var pass = await context.Passes.AsNoTracking().OrderBy(x => x.Id).FirstAsync();
        var classIds = await context.Classes.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToArrayAsync();
        await using var held = await HeldLock.AcquireAsync(db.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classIds[1]} FOR UPDATE");

        var run = runner.RunAsync(Operation, async ct =>
        {
            var tracked = await unitOfWork.Pass.GetByIdAsync(pass.Id, ct);
            tracked!.RemainingVisits += 1;
            await unitOfWork.SaveChangesAsync(ct);
            await context.Classes.FromSql($"SELECT * FROM classes WHERE id = {classIds[0]} FOR NO KEY UPDATE").ToListAsync(ct);
            await context.Classes.FromSql($"SELECT * FROM classes WHERE id = {classIds[1]} FOR NO KEY UPDATE").ToListAsync(ct);
            return TransactionResult.Commit(0);
        });
        await held.WaitForBlockedAsync(1);
        await held.ExecuteAsync($"SELECT 1 FROM classes WHERE id = {classIds[0]} FOR UPDATE");

        var exception = await Assert.ThrowsExceptionAsync<TransactionConflictException>(() => run);

        Assert.AreEqual(Operation, exception.Operation);
        Assert.AreEqual("deadlock", exception.Reason);
        Assert.AreEqual(PostgresErrorCodes.DeadlockDetected, ((PostgresException)exception.InnerException!.InnerException!).SqlState);
        Assert.AreEqual(pass.RemainingVisits, await RemainingVisitsAsync(db, pass.Id));
    }

    [TestMethod]
    public async Task RunAsync_UniqueViolation_ThrowsDbUpdateExceptionUnchanged()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var (runner, unitOfWork) = CreateRunner(context);
        var pass = await context.Passes.AsNoTracking().OrderBy(x => x.Id).FirstAsync();

        var exception = await Assert.ThrowsExceptionAsync<DbUpdateException>(() =>
            runner.RunAsync(Operation, async ct =>
            {
                await unitOfWork.Pass.CreateAsync(new Pass { UserId = pass.UserId, RemainingVisits = 1, ValidUntil = pass.ValidUntil }, ct);
                await unitOfWork.SaveChangesAsync(ct);
                return TransactionResult.Commit(0);
            }));

        Assert.AreEqual(PostgresErrorCodes.UniqueViolation, ((PostgresException)exception.InnerException!).SqlState);
    }

    private static (TransactionRunner Runner, IUnitOfWork UnitOfWork) CreateRunner(
        ClubDbContext context,
        TimeSpan? lockTimeout = null)
    {
        var unitOfWork = new UnitOfWork(context);
        var settings = new TransactionSettings { LockTimeout = lockTimeout ?? LockTimeout };
        var runner = new TransactionRunner(unitOfWork, Options.Create(settings));

        return (runner, unitOfWork);
    }

    private static async Task<string> ShowLockTimeoutAsync(ClubDbContext context, CancellationToken ct)
    {
        return await context.Database.SqlQueryRaw<string>("SELECT current_setting('lock_timeout') AS \"Value\"").SingleAsync(ct);
    }

    private static async Task<int> RemainingVisitsAsync(TestDb db, long passId)
    {
        await using var context = db.CreateContext();

        return await context.Passes.Where(x => x.Id == passId).Select(x => x.RemainingVisits).SingleAsync();
    }
}
