using System.Security.Cryptography;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Idempotency;

[TestClass]
public class IdempotencyKeyRepositoryTests
{
    [TestMethod]
    public async Task ClaimAsync_NewKey_InsertsRowAndReturnsIsNew()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var userId = await UserIdAsync(context);
        var hash = Hash();

        var claim = await new UnitOfWork(context).IdempotencyKey.ClaimAsync(Candidate(userId, hash, 11, 2));

        Assert.IsTrue(claim.IsNew);
        await using var check = db.CreateContext();
        var row = await check.IdempotencyKeys.AsNoTracking().SingleAsync();
        Assert.AreEqual(claim.Key.Id, row.Id);
        Assert.AreEqual(userId, row.UserId);
        Assert.AreEqual(IdempotentOperation.Enroll, row.Operation);
        CollectionAssert.AreEqual(hash, row.KeyHash);
        Assert.AreEqual(11L, row.ClassId);
        Assert.AreEqual(2, row.Seats);
        Assert.IsNull(row.ResultId);
        Assert.IsNull(row.ResultCreatedAt);
        Assert.AreEqual(
            "enroll",
            await check.Database.SqlQuery<string>($"SELECT operation AS \"Value\" FROM idempotency_keys").SingleAsync());
    }

    [TestMethod]
    public async Task ClaimAsync_NewKey_ReturnedRowIsTrackedForResult()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var userId = await UserIdAsync(context);
        var createdAt = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560);

        var claim = await new UnitOfWork(context).IdempotencyKey.ClaimAsync(Candidate(userId, Hash(), 11, 2));
        claim.Key.ResultId = 77;
        claim.Key.ResultCreatedAt = createdAt;
        await context.SaveChangesAsync();

        await using var check = db.CreateContext();
        var row = await check.IdempotencyKeys.AsNoTracking().SingleAsync();
        Assert.AreEqual(77L, row.ResultId);
        Assert.AreEqual(createdAt, row.ResultCreatedAt);
    }

    [TestMethod]
    public async Task ClaimAsync_ExistingKey_ReturnsStoredRowAndChangesNothing()
    {
        await using var db = await TestDb.CloneAsync();
        var hash = Hash();
        var createdAt = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560);
        long userId;
        IdempotencyKey stored;

        await using (var setup = db.CreateContext())
        {
            userId = await UserIdAsync(setup);
            stored = Candidate(userId, hash, 11, 2);
            stored.ResultId = 77;
            stored.ResultCreatedAt = createdAt;
            setup.IdempotencyKeys.Add(stored);
            await setup.SaveChangesAsync();
        }

        await using var context = db.CreateContext();

        var claim = await new UnitOfWork(context).IdempotencyKey.ClaimAsync(Candidate(userId, hash, 12, 3));

        Assert.IsFalse(claim.IsNew);
        Assert.AreEqual(Describe(stored), Describe(claim.Key));
        await using var check = db.CreateContext();
        var rows = await check.IdempotencyKeys.AsNoTracking().ToListAsync();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(Describe(stored), Describe(rows[0]));
    }

    [TestMethod]
    public async Task ClaimAsync_SameHashOtherUser_InsertsSeparateRow()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var userIds = await context.Users.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToListAsync();
        var hash = Hash();
        var repository = new UnitOfWork(context).IdempotencyKey;

        var first = await repository.ClaimAsync(Candidate(userIds[0], hash, 11, 2));
        var second = await repository.ClaimAsync(Candidate(userIds[1], hash, 12, 3));

        Assert.IsTrue(first.IsNew);
        Assert.IsTrue(second.IsNew);
        Assert.AreNotEqual(first.Key.Id, second.Key.Id);
        await using var check = db.CreateContext();
        var rows = await check.IdempotencyKeys.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        CollectionAssert.AreEqual(
            new[] { $"{userIds[0]}|11|2", $"{userIds[1]}|12|3" },
            rows.Select(x => $"{x.UserId}|{x.ClassId}|{x.Seats}").ToArray());
    }

    [TestMethod]
    public async Task ClaimAsync_SameHashOtherOperation_InsertsSeparateRow()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var userId = await UserIdAsync(context);
        var hash = Hash();
        var repository = new UnitOfWork(context).IdempotencyKey;
        var waitlist = Candidate(userId, hash, 12, 3);
        waitlist.Operation = IdempotentOperation.JoinWaitlist;

        var first = await repository.ClaimAsync(Candidate(userId, hash, 11, 2));
        var second = await repository.ClaimAsync(waitlist);

        Assert.IsTrue(first.IsNew);
        Assert.IsTrue(second.IsNew);
        await using var check = db.CreateContext();
        CollectionAssert.AreEqual(
            new[] { "enroll|11|2", "join-waitlist|12|3" },
            await check.Database.SqlQuery<string>(
                    $"SELECT operation || '|' || class_id || '|' || seats AS \"Value\" FROM idempotency_keys ORDER BY id")
                .ToArrayAsync());
    }

    [TestMethod]
    public async Task ClaimAsync_OtherHashSameUser_InsertsSeparateRow()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var userId = await UserIdAsync(context);
        var repository = new UnitOfWork(context).IdempotencyKey;

        var first = await repository.ClaimAsync(Candidate(userId, Hash(), 11, 2));
        var second = await repository.ClaimAsync(Candidate(userId, Hash(), 11, 2));

        Assert.IsTrue(first.IsNew);
        Assert.IsTrue(second.IsNew);
        await using var check = db.CreateContext();
        Assert.AreEqual(2, await check.IdempotencyKeys.CountAsync());
    }

    [TestMethod]
    public async Task SaveChanges_SameUserOperationAndHash_ViolatesUniqueIndex()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var userId = await UserIdAsync(context);
        var hash = Hash();

        context.IdempotencyKeys.Add(Candidate(userId, hash, 11, 2));
        context.IdempotencyKeys.Add(Candidate(userId, hash, 12, 3));

        await AssertViolationAsync(context, PostgresErrorCodes.UniqueViolation, "IX_idempotency_keys_user_id_operation_key_hash");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(31)]
    [DataRow(33)]
    public async Task SaveChanges_KeyHashNot32Bytes_ViolatesCheckConstraint(int length)
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var userId = await UserIdAsync(context);

        context.IdempotencyKeys.Add(Candidate(userId, new byte[length], 11, 2));

        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation, "ck_idempotency_keys_key_hash");
    }

    [TestMethod]
    public async Task SaveChanges_UnknownUser_ViolatesForeignKey()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();

        context.IdempotencyKeys.Add(Candidate(999999999, Hash(), 11, 2));

        await AssertViolationAsync(context, PostgresErrorCodes.ForeignKeyViolation, "FK_idempotency_keys_users_user_id");
    }

    [TestMethod]
    [DataRow("Enroll")]
    [DataRow("waitlist")]
    public async Task Insert_UnknownOperation_ViolatesCheckConstraint(string operation)
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var userId = await UserIdAsync(context);
        var hash = Hash();

        var exception = await Assert.ThrowsExceptionAsync<PostgresException>(() => context.Database.ExecuteSqlAsync(
            $"INSERT INTO idempotency_keys (user_id, operation, key_hash, class_id, seats) VALUES ({userId}, {operation}, {hash}, 11, 2)"));

        Assert.AreEqual(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.AreEqual("ck_idempotency_keys_operation", exception.ConstraintName);
    }

    [TestMethod]
    public async Task Read_UnknownOperationInDatabase_Throws()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var userId = await UserIdAsync(context);
        var hash = Hash();
        await context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE idempotency_keys DROP CONSTRAINT ck_idempotency_keys_operation");
        await context.Database.ExecuteSqlAsync(
            $"INSERT INTO idempotency_keys (user_id, operation, key_hash, class_id, seats) VALUES ({userId}, 'waitlist', {hash}, 11, 2)");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => context.IdempotencyKeys.AsNoTracking().ToListAsync());
    }

    private static IdempotencyKey Candidate(long userId, byte[] hash, long classId, int seats)
    {
        return new IdempotencyKey
        {
            UserId = userId,
            Operation = IdempotentOperation.Enroll,
            KeyHash = hash,
            ClassId = classId,
            Seats = seats
        };
    }

    private static byte[] Hash()
    {
        return RandomNumberGenerator.GetBytes(32);
    }

    private static string Describe(IdempotencyKey x)
    {
        return $"{x.Id}|{x.UserId}|{x.Operation}|{Convert.ToHexString(x.KeyHash)}|{x.ClassId}|{x.Seats}|{x.ResultId}|{x.ResultCreatedAt:O}";
    }

    private static async Task<long> UserIdAsync(ClubDbContext context)
    {
        return await context.Users.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
    }

    private static async Task AssertViolationAsync(ClubDbContext context, string sqlState, string constraint)
    {
        var exception = await Assert.ThrowsExceptionAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var postgres = (PostgresException)exception.InnerException!;

        Assert.AreEqual(sqlState, postgres.SqlState);
        Assert.AreEqual(constraint, postgres.ConstraintName);
    }
}
