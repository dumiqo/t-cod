using DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WebApi.Tests.Infrastructure;

public sealed class ScratchDatabase : IAsyncDisposable
{
    private readonly string _name;

    private ScratchDatabase(string name, string connectionString)
    {
        _name = name;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<ScratchDatabase> CreateAsync()
    {
        var name = $"club_scratch_{Guid.NewGuid():N}";

        await ExecuteOnServerAsync($"CREATE DATABASE \"{name}\"");

        var connectionString = new NpgsqlConnectionStringBuilder(TestDatabase.ServerConnectionString)
        {
            Database = name
        }.ConnectionString;

        return new ScratchDatabase(name, connectionString);
    }

    public ClubDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ClubDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new ClubDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));

        await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
    }

    private static async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.ServerConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
