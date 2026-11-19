using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Harness;

[TestClass]
public class HeldLockTests
{
    [TestMethod]
    public async Task WaitForBlockedCountAsync_TwoQueuedSessions_ReturnsTwo()
    {
        await using var db = await TestDb.CloneAsync();
        var classId = await FirstClassIdAsync(db);
        var sql = $"SELECT 1 FROM classes WHERE id = {classId} FOR UPDATE";
        await using var held = await HeldLock.AcquireAsync(db.ConnectionString, sql);
        await using var first = await OpenAsync(db.ConnectionString);
        await using var second = await OpenAsync(db.ConnectionString);
        var waiting = new[] { RunAsync(first, sql), RunAsync(second, sql) };

        var blocked = await held.WaitForBlockedCountAsync(2);

        await held.DisposeAsync();
        await Task.WhenAll(waiting);
        Assert.AreEqual(2L, blocked);
    }

    [TestMethod]
    public async Task WaitForBlockedCountAsync_NobodyQueued_ReturnsZeroAfterTimeout()
    {
        await using var db = await TestDb.CloneAsync();
        var classId = await FirstClassIdAsync(db);
        await using var held = await HeldLock.AcquireAsync(db.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR UPDATE");

        var blocked = await held.WaitForBlockedCountAsync(1, TimeSpan.FromMilliseconds(300));

        Assert.AreEqual(0L, blocked);
    }

    private static async Task<long> FirstClassIdAsync(TestDb db)
    {
        await using var context = db.CreateContext();

        return await context.Classes.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        return connection;
    }

    private static async Task RunAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
