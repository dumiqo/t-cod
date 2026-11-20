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
    public async Task RateLimit_RedisBroken_LocalJournalRejects61stAndFailuresAreLogged()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        app.BreakRedis();

        await ScheduleAsync(app, client, 60);
        await AssertRejectedAsync(app, client, "60");

        var failures = app.Logs.GetSnapshot().Where(x => x.Id.Id == 1005).ToList();
        Assert.AreEqual(60, failures.Count);
        foreach (var failure in failures)
        {
            Assert.AreEqual("RedisFailure", failure.Id.Name);
            Assert.AreEqual(LogLevel.Warning, failure.Level);
            Assert.AreEqual("rate-limit", failure.GetStructuredStateValue("Operation"));
            Assert.IsInstanceOfType<RedisConnectionException>(failure.Exception);
        }

        var measurements = app.Metrics("club.redis.failures").GetMeasurementSnapshot();
        Assert.AreEqual(60, measurements.Count);
        Assert.IsTrue(measurements.All(x => x.Value == 1 && (string?)x.Tags["operation"] == "rate-limit"));
        var rejection = app.Logs.GetSnapshot().Single(x => x.Id.Id == 1003);
        Assert.AreEqual(user.Id.ToString(), rejection.GetStructuredStateValue("UserId"));
        Assert.AreEqual("60", rejection.GetStructuredStateValue("RetryAfter"));
        var rejected = app.Metrics("club.requests.rejected").GetMeasurementSnapshot().Single();
        Assert.AreEqual(1, rejected.Value);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, object?> { ["operation"] = "rate-limit", ["reason"] = "too-many-requests" },
            rejected.Tags.ToDictionary(x => x.Key, x => x.Value));
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
    public async Task RateLimit_RedisRestored_OutageRequestsStillCountAndLeaveWindowAfter60s()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        app.BreakRedis();
        await ScheduleAsync(app, client, 60);

        app.RestoreRedis();
        await AssertRejectedAsync(app, client, "60");

        Assert.IsFalse(await app.Redis.KeyExistsAsync($"rate:{user.Id}"));
        app.Clock.Advance(TimeSpan.FromSeconds(60));
        await ScheduleAsync(app, client, 1);
    }

    [TestMethod]
    public async Task RateLimit_RedisBrokenMidWindow_RequestsBeforeOutageStillCount()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await ScheduleAsync(app, client, 30);
        app.Clock.Advance(TimeSpan.FromSeconds(10));

        app.BreakRedis();
        await ScheduleAsync(app, client, 30);

        await AssertRejectedAsync(app, client, "50");
    }

    [TestMethod]
    public async Task RateLimit_RedisBroken_WindowBoundaryRejectsAt59999MsAndAcceptsAt60000Ms()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        app.BreakRedis();
        await ScheduleAsync(app, client, 60);

        app.Clock.Advance(TimeSpan.FromMilliseconds(59_999));
        await AssertRejectedAsync(app, client, "1");

        app.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await ScheduleAsync(app, client, 1);
    }

    [TestMethod]
    public async Task RateLimit_LocalRejectionWithRedisUp_LeavesRedisJournalUntouched()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await ScheduleAsync(app, client, 30);
        app.BreakRedis();
        await ScheduleAsync(app, client, 30);
        app.RestoreRedis();

        await AssertRejectedAsync(app, client, "60");

        Assert.AreEqual(30, await app.Redis.SortedSetLengthAsync($"rate:{user.Id}"));
    }

    [TestMethod]
    public async Task RateLimit_RedisRejects_LocalReservationIsReleased()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var now = app.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        var key = $"rate:{user.Id}";
        await app.Redis.SortedSetAddAsync(
            key,
            Enumerable.Range(0, 60).Select(i => new SortedSetEntry($"{now}:other{i}", now)).ToArray());

        await AssertRejectedAsync(app, client, "60");

        await app.Redis.KeyDeleteAsync(key);
        await ScheduleAsync(app, client, 60);
        await AssertRejectedAsync(app, client, "60");
    }

    [TestMethod]
    public async Task RateLimit_RedisBroken_DifferentUsersHaveSeparateJournals()
    {
        await using var app = await TestApp.StartAsync();
        var first = await app.Data.UserAsync();
        var second = await app.Data.UserAsync();
        using var firstClient = await app.CreateClientAsync(first.Email);
        using var secondClient = await app.CreateClientAsync(second.Email);
        app.BreakRedis();

        await ScheduleAsync(app, firstClient, 60);
        await AssertRejectedAsync(app, firstClient, "60");

        await ScheduleAsync(app, secondClient, 60);
        await AssertRejectedAsync(app, secondClient, "60");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task RateLimit_RedisBroken_70ConcurrentRequestsExactly60Pass()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        app.BreakRedis();

        var responses = await Task.WhenAll(Enumerable.Range(0, 70)
            .Select(_ => ScheduleApi.GetAsync(client, Now(app), Now(app).AddDays(1))));

        try
        {
            Assert.AreEqual(60, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.AreEqual(10, responses.Count(x => x.StatusCode == HttpStatusCode.TooManyRequests));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
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

    private static async Task AssertRejectedAsync(TestApp app, HttpClient client, string retryAfter)
    {
        using var response = await ScheduleApi.GetAsync(client, Now(app), Now(app).AddDays(1));
        Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(retryAfter, response.Headers.GetValues("Retry-After").Single());
    }
}
