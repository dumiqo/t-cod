using DataAccessLayer;
using DataAccessLayer.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace WebApi.Tests.Infrastructure;

public sealed class TestDb : IAsyncDisposable
{
    private static readonly TimeSpan EarliestClassBeforeSeed = TimeSpan.FromMinutes(100 * 432);

    private static readonly string TemplateName = $"club_template_{Guid.NewGuid():N}";

    private static readonly Lazy<Task<DateTimeOffset>> Template = new(BuildTemplateAsync);

    private readonly string _name;

    private TestDb(string name, DateTimeOffset seededAt)
    {
        _name = name;
        ConnectionString = ConnectionStringFor(name);
        SeededAt = seededAt;
    }

    public string ConnectionString { get; }

    public DateTimeOffset SeededAt { get; }

    public static async Task<TestDb> CloneAsync()
    {
        var seededAt = await Template.Value;
        var name = $"club_clone_{Guid.NewGuid():N}";

        await ExecuteOnServerAsync($"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateName}\"");

        return new TestDb(name, seededAt);
    }

    public ClubDbContext CreateContext()
    {
        return CreateContext(ConnectionString);
    }

    public ClubDbContext CreateContext(params IInterceptor[] interceptors)
    {
        return CreateContext(ConnectionString, interceptors);
    }

    public async ValueTask DisposeAsync()
    {
        await DropAsync(_name);
    }

    internal static async Task DropTemplateAsync()
    {
        if (!Template.IsValueCreated)
        {
            return;
        }

        await DropAsync(TemplateName);
    }

    private static async Task<DateTimeOffset> BuildTemplateAsync()
    {
        var connectionString = ConnectionStringFor(TemplateName);

        await ExecuteOnServerAsync($"CREATE DATABASE \"{TemplateName}\"");

        DateTime earliestClass;

        await using (var context = CreateContext(connectionString))
        {
            await context.Database.MigrateAsync();
            await DatabaseSeeder.SeedAsync(context);

            // решение: минимум по всем занятиям — самое раннее сидер отменяет, и с фильтром !IsCancelled
            // часы ушли бы на 432 минуты назад от момента засева.
            earliestClass = await context.Classes.IgnoreQueryFilters().MinAsync(x => x.StartsAt);
        }

        NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));

        return new DateTimeOffset(earliestClass, TimeSpan.Zero) + EarliestClassBeforeSeed;
    }

    private static ClubDbContext CreateContext(string connectionString, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ClubDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(interceptors)
            .Options;

        return new ClubDbContext(options);
    }

    private static async Task DropAsync(string name)
    {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionStringFor(name)));

        await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
    }

    private static string ConnectionStringFor(string name)
    {
        return new NpgsqlConnectionStringBuilder(TestDatabase.ServerConnectionString)
        {
            Database = name
        }.ConnectionString;
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
