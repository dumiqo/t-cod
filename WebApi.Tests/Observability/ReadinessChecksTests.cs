using System.Net;
using System.Net.Sockets;
using DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Npgsql;
using StackExchange.Redis;
using WebApi.Observability;

namespace WebApi.Tests.Observability;

[TestClass]
public class ReadinessChecksTests
{
    [TestMethod]
    public async Task CheckHealthAsync_DependenciesOnClosedPort_PostgresUnhealthyRedisDegraded()
    {
        var port = ClosedPort();
        var redis = RedisOptions(port);
        redis.BacklogPolicy = BacklogPolicy.FailFast;
        await using var provider = await BuildAsync(port, redis);

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        CollectionAssert.AreEquivalent(new[] { "postgres", "redis" }, report.Entries.Keys.ToArray());
        Assert.AreEqual(HealthStatus.Unhealthy, report.Status);

        var postgres = report.Entries["postgres"];
        Assert.AreEqual(HealthStatus.Unhealthy, postgres.Status);
        Assert.IsInstanceOfType<NpgsqlException>(postgres.Exception);
        CollectionAssert.AreEqual(new[] { "ready" }, postgres.Tags.ToArray());

        var cache = report.Entries["redis"];
        Assert.AreEqual(HealthStatus.Degraded, cache.Status);
        Assert.IsInstanceOfType<RedisConnectionException>(cache.Exception);
        CollectionAssert.AreEqual(new[] { "ready" }, cache.Tags.ToArray());

        var logs = provider.GetRequiredService<FakeLogCollector>()
            .GetSnapshot()
            .Where(x => x.Category == typeof(HealthCheckService).Namespace + ".DefaultHealthCheckService")
            .Where(x => x.Level >= LogLevel.Warning)
            .ToList();

        CollectionAssert.AreEquivalent(
            new[] { "postgres", "redis" },
            logs.Select(x => x.GetStructuredStateValue("HealthCheckName")).ToArray());

        var postgresLog = logs.Single(x => x.GetStructuredStateValue("HealthCheckName") == "postgres");
        Assert.AreEqual("HealthCheckEnd", postgresLog.Id.Name);
        Assert.AreEqual(LogLevel.Error, postgresLog.Level);
        Assert.AreSame(postgres.Exception, postgresLog.Exception);

        var cacheLog = logs.Single(x => x.GetStructuredStateValue("HealthCheckName") == "redis");
        Assert.AreEqual("HealthCheckEnd", cacheLog.Id.Name);
        Assert.AreEqual(LogLevel.Warning, cacheLog.Level);
        Assert.AreSame(cache.Exception, cacheLog.Exception);
    }

    [TestMethod]
    public async Task CheckHealthAsync_DependenciesSilent_BothCutAtThreeSeconds()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        var redis = RedisOptions(port);
        redis.AsyncTimeout = 10_000;
        await using var provider = await BuildAsync(port, redis);

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        CollectionAssert.AreEquivalent(new[] { "postgres", "redis" }, report.Entries.Keys.ToArray());
        Assert.AreEqual(HealthStatus.Unhealthy, report.Entries["postgres"].Status);
        Assert.AreEqual(HealthStatus.Degraded, report.Entries["redis"].Status);

        Assert.IsInstanceOfType<NpgsqlException>(report.Entries["postgres"].Exception);
        Assert.IsInstanceOfType<TimeoutException>(report.Entries["postgres"].Exception?.InnerException);
        Assert.IsInstanceOfType<OperationCanceledException>(report.Entries["redis"].Exception);

        foreach (var (name, entry) in report.Entries)
        {
            Assert.IsTrue(
                entry.Duration >= TimeSpan.FromSeconds(2.9) && entry.Duration < TimeSpan.FromSeconds(6),
                $"{name}: {entry.Duration}");
        }
    }

    private static async Task<ServiceProvider> BuildAsync(int postgresPort, ConfigurationOptions redis)
    {
        var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis);
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddFakeLogging();
        services.AddDbContext<ClubDbContext>(options => options.UseNpgsql(
            $"Host=127.0.0.1;Port={postgresPort};Database=club;Username=club;Password=club;Timeout=10"));
        services.AddSingleton<IConnectionMultiplexer>(_ => multiplexer);
        services.AddHealthChecks().AddClubReadinessChecks();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static ConfigurationOptions RedisOptions(int port)
    {
        return new ConfigurationOptions
        {
            EndPoints = { { IPAddress.Loopback, port } },
            AbortOnConnectFail = false,
            ConnectTimeout = 200
        };
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return port;
    }
}
