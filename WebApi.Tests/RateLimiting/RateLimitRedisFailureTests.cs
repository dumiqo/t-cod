using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WebApi.Redis;
using WebApi.Tests.Infrastructure;
using WebApi.Tests.Schedule;

namespace WebApi.Tests.RateLimiting;

[TestClass]
public class RateLimitRedisFailureTests
{
    [TestMethod]
    public async Task RateLimit_RedisBroken_RequestsOverLimitPassAndFailuresAreLogged()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        app.BreakRedis();

        await ScheduleAsync(app, client, 70);

        var failures = app.Logs.GetSnapshot().Where(x => x.Id.Id == 1005).ToList();
        Assert.AreEqual(70, failures.Count);
        foreach (var failure in failures)
        {
            Assert.AreEqual("RedisFailure", failure.Id.Name);
            Assert.AreEqual(LogLevel.Warning, failure.Level);
            Assert.AreEqual("rate-limit", failure.GetStructuredStateValue("Operation"));
            Assert.IsInstanceOfType<RedisConnectionException>(failure.Exception);
        }

        var measurements = app.Metrics("club.redis.failures").GetMeasurementSnapshot();
        Assert.AreEqual(70, measurements.Count);
        Assert.IsTrue(measurements.All(x => x.Value == 1 && (string?)x.Tags["operation"] == "rate-limit"));
        Assert.IsFalse(app.Logs.GetSnapshot().Any(x => x.Id.Id == 1003));
        Assert.AreEqual(0, app.Metrics("club.requests.rejected").GetMeasurementSnapshot().Count);
    }

    [TestMethod]
    public async Task RateLimit_RedisHung_RequestPassesWithinOperationTimeout()
    {
        await using var proxy = FreezableRedisProxy.Start(TestDatabase.RedisConnectionString);
        await using var app = await TestApp.StartAsync(builder => builder
            .UseSetting("Redis:OperationTimeout", "00:00:00.200")
            .ConfigureTestServices(services => services.AddSingleton(_ => KeyPrefixedConnection.Wrap(
                RedisConnection.Connect(proxy.ConnectionString),
                $"test:{Guid.NewGuid():N}:"))));
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await ScheduleAsync(app, client, 1);
        proxy.Freeze();

        var watch = Stopwatch.StartNew();
        await ScheduleAsync(app, client, 1);

        Assert.IsTrue(
            watch.Elapsed >= TimeSpan.FromMilliseconds(190) && watch.Elapsed < TimeSpan.FromMilliseconds(1200),
            $"elapsed {watch.Elapsed}");
        var failure = app.Logs.GetSnapshot().Single(x => x.Id.Id == 1005);
        Assert.AreEqual("rate-limit", failure.GetStructuredStateValue("Operation"));
        Assert.IsInstanceOfType<TimeoutException>(failure.Exception);
        Assert.AreEqual("rate-limit", (string?)app.Metrics("club.redis.failures").GetMeasurementSnapshot().Single().Tags["operation"]);
    }

    [TestMethod]
    public async Task RateLimit_RedisRestored_RequestsDuringOutageAreNotJournaled()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        app.BreakRedis();
        await ScheduleAsync(app, client, 70);

        app.RestoreRedis();
        await ScheduleAsync(app, client, 60);
        using var rejected = await ScheduleApi.GetAsync(client, Now(app), Now(app).AddDays(1));

        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.AreEqual("60", rejected.Headers.GetValues("Retry-After").Single());
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }

    private static async Task ScheduleAsync(TestApp app, HttpClient client, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var response = await ScheduleApi.GetAsync(client, Now(app), Now(app).AddDays(1));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"request {i + 1}");
        }
    }
}
