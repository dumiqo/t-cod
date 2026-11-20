using Npgsql;

namespace WebApi.Tests.Infrastructure;

public sealed class HeldLock : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private HeldLock(string connectionString, NpgsqlConnection connection, NpgsqlTransaction transaction, int processId)
    {
        _connectionString = connectionString;
        _connection = connection;
        _transaction = transaction;
        ProcessId = processId;
    }

    public int ProcessId { get; }

    public static async Task<HeldLock> AcquireAsync(string connectionString, string sql)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        var heldLock = new HeldLock(connectionString, connection, transaction, connection.ProcessID);

        await heldLock.ExecuteAsync(sql);

        return heldLock;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var command = new NpgsqlCommand(sql, _connection, _transaction);
        await command.ExecuteNonQueryAsync();
    }

    public async Task WaitForBlockedAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (await CountBlockedAsync() < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Fewer than {count} sessions are blocked by process {ProcessId}.");
            }

            await Task.Delay(20);
        }
    }

    public async Task<long> WaitForBlockedCountAsync(int expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var count = await CountBlockedAsync();

        while (count < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            count = await CountBlockedAsync();
        }

        return count;
    }

    public Task CommitAsync()
    {
        return _transaction.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<long> CountBlockedAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            WITH RECURSIVE blocked(pid) AS (
                SELECT pid FROM pg_stat_activity WHERE @holder = ANY(pg_blocking_pids(pid))
                UNION
                SELECT a.pid FROM pg_stat_activity a JOIN blocked b ON b.pid = ANY(pg_blocking_pids(a.pid))
            )
            SELECT count(*) FROM blocked
            """,
            connection);
        command.Parameters.AddWithValue("holder", ProcessId);

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
