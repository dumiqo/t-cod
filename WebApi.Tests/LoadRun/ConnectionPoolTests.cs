using Microsoft.Extensions.Configuration;
using Npgsql;

namespace WebApi.Tests.LoadRun;

[TestClass]
public class ConnectionPoolTests
{
    [TestMethod]
    public async Task ShippedConnectionString_PoolAndReadinessFitServerSlots()
    {
        var shipped = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();
        var pool = new NpgsqlConnectionStringBuilder(shipped.GetConnectionString("Postgres")).MaxPoolSize;

        await using var connection = new NpgsqlConnection(TestDatabase.PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('max_connections')::int - current_setting('superuser_reserved_connections')::int - current_setting('reserved_connections')::int",
            connection);
        var slots = (int)(await command.ExecuteScalarAsync())!;

        Assert.IsTrue(pool + 1 <= slots, $"pool {pool} + readiness connection exceeds {slots} server slots");
    }
}
