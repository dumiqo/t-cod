using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Harness;

[TestClass]
public class TestDbTests
{
    [TestMethod]
    public async Task CloneAsync_NewClone_ContainsSeedData()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();

        Assert.AreEqual(205, await context.Users.CountAsync());
        Assert.AreEqual(200, await context.Classes.IgnoreQueryFilters().CountAsync());
    }

    [TestMethod]
    public async Task DisposeAsync_AfterQueries_DropsDatabase()
    {
        var db = await TestDb.CloneAsync();
        var name = DatabaseName(db.ConnectionString);

        await using (var context = db.CreateContext())
        {
            await context.Users.CountAsync();
        }

        Assert.IsTrue(await DatabaseExistsAsync(name));

        await db.DisposeAsync();

        Assert.IsFalse(await DatabaseExistsAsync(name));
    }

    [TestMethod]
    public async Task CloneAsync_EightInParallel_AllSeededAndDistinct()
    {
        var clones = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => TestDb.CloneAsync()));

        try
        {
            var names = new List<string>();

            foreach (var clone in clones)
            {
                await using var context = clone.CreateContext();

                Assert.AreEqual(205, await context.Users.CountAsync());

                names.Add(await context.Database.SqlQueryRaw<string>("SELECT current_database() AS \"Value\"").SingleAsync());
            }

            Assert.AreEqual(8, names.Distinct().Count());
        }
        finally
        {
            foreach (var clone in clones)
            {
                await clone.DisposeAsync();
            }
        }
    }

    internal static string DatabaseName(string connectionString)
    {
        return new NpgsqlConnectionStringBuilder(connectionString).Database
            ?? throw new InvalidOperationException("Connection string has no database.");
    }

    internal static async Task<bool> DatabaseExistsAsync(string name)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.ServerConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_database WHERE datname = @name";
        command.Parameters.AddWithValue("name", name);

        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }
}
