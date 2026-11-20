using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using DataAccessLayer.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebApi.CardCache;
using WebApi.Redis;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.CardCache;

[TestClass]
public class CardCacheRedisFailureTests
{
    [TestMethod]
    public async Task Start_RedisDownFromStart_StartsLogsConnectAndCardComesFromDb()
    {
        await using var app = await TestApp.StartAsync(builder => builder
            .UseSetting("Redis:OperationTimeout", "00:00:04")
            .ConfigureTestServices(services =>
                services.AddSingleton(_ => RedisConnection.Connect(KeyPrefixedConnection.ClosedEndpoint()))));
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);
        var connect = Failures(app).Single();
        Assert.AreEqual("connect", connect.GetStructuredStateValue("Operation"));
        Assert.AreEqual(LogLevel.Warning, connect.Level);
        Assert.AreEqual("RedisFailure", connect.Id.Name);
        Assert.IsNull(connect.Exception);
        StringAssert.Contains(connect.Message, "not connected");

        var watch = Stopwatch.StartNew();
        var card = await CardAsync(client, id);

        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(1), $"elapsed {watch.Elapsed}");
        Assert.AreEqual(2, (int)card["seats"]!);
        Assert.AreEqual("active", (string)card["status"]!);
        AssertFailures(
            app,
            ("connect", null),
            ("rate-limit", typeof(RedisConnectionException)),
            ("cache-get", typeof(RedisConnectionException)));
        AssertFailureMetric(app, "rate-limit", "cache-get");
    }

    [TestMethod]
    public async Task GetCard_RedisHung_ReturnsDbCardWithinOperationTimeout()
    {
        await using var proxy = FreezableRedisProxy.Start(TestDatabase.RedisConnectionString);
        await using var app = await TestApp.StartAsync(builder => builder
            .UseSetting("Redis:OperationTimeout", "00:00:00.200")
            .ConfigureTestServices(services => services.AddSingleton(_ => KeyPrefixedConnection.Wrap(
                RedisConnection.Connect(proxy.ConnectionString),
                $"test:{Guid.NewGuid():N}:"))));
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);
        Assert.AreEqual(2, (int)(await CardAsync(client, id))["seats"]!);
        Assert.IsTrue(await app.Redis.HashExistsAsync(Key(user), id.ToString()));
        await SetSeatsBypassingApiAsync(app, id, 5);
        proxy.Freeze();

        var watch = Stopwatch.StartNew();
        var card = await CardAsync(client, id);

        Assert.AreEqual(5, (int)card["seats"]!);
        Assert.IsTrue(
            watch.Elapsed >= TimeSpan.FromMilliseconds(190) && watch.Elapsed < TimeSpan.FromMilliseconds(1200),
            $"elapsed {watch.Elapsed}");
        AssertFailures(app, ("rate-limit", typeof(TimeoutException)), ("cache-get", typeof(TimeoutException)));
        AssertFailureMetric(app, "rate-limit", "cache-get");
    }

    [TestMethod]
    public async Task GetCard_RedisBroken_ReturnsDbCardAndDoesNotFill()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);
        Assert.AreEqual(2, (int)(await CardAsync(client, id))["seats"]!);
        await SetSeatsBypassingApiAsync(app, id, 5);
        app.BreakRedis();

        var card = await CardAsync(client, id);

        Assert.AreEqual(5, (int)card["seats"]!);
        AssertFailures(app, ("rate-limit", typeof(RedisConnectionException)), ("cache-get", typeof(RedisConnectionException)));
        AssertFailureMetric(app, "rate-limit", "cache-get");
        app.RestoreRedis();
        var snapshot = JsonNode.Parse((string)(await app.Redis.HashGetAsync(Key(user), id.ToString()))!)!;
        Assert.AreEqual(2, (int)snapshot["Seats"]!);
        Assert.AreEqual(2, (int)(await CardAsync(client, id))["seats"]!);
    }

    [TestMethod]
    public async Task GetCard_FillFails_Returns200AndLogsCacheSet()
    {
        await using var app = await TestApp.StartAsync(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ICardCache>(provider => new BreakOnFillCardCache(
                ActivatorUtilities.CreateInstance<WebApi.CardCache.CardCache>(provider),
                KeyPrefixedConnection.Of(provider.GetRequiredService<IConnectionMultiplexer>())))));
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 3);
        using var client = await app.CreateClientAsync(user.Email);

        var card = await CardAsync(client, id);

        Assert.AreEqual(3, (int)card["seats"]!);
        AssertFailures(app, ("cache-set", typeof(RedisConnectionException)));
        AssertFailureMetric(app, "cache-set");
        app.RestoreRedis();
        Assert.IsFalse(await app.Redis.KeyExistsAsync(Key(user)));
    }

    [TestMethod]
    public async Task Cancel_RedisBroken_Returns200AndCardsAreCancelledDuringAndAfterOutage()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);
        Assert.AreEqual("active", (string)(await CardAsync(client, id))["status"]!);
        app.BreakRedis();

        using (var cancel = await CancelAsync(client, id))
        {
            await AssertCancelledAsync(cancel, id, 2);
        }

        AssertFailures(app, ("rate-limit", typeof(RedisConnectionException)), ("cache-remove", typeof(RedisConnectionException)));
        Assert.AreEqual("cancelled", (string)(await CardAsync(client, id))["status"]!);
        AssertFailures(
            app,
            ("rate-limit", typeof(RedisConnectionException)),
            ("cache-remove", typeof(RedisConnectionException)),
            ("rate-limit", typeof(RedisConnectionException)),
            ("cache-remove", typeof(RedisConnectionException)));
        app.RestoreRedis();
        await AssertHashAsync(app, user, "0", id);

        var card = await CardAsync(client, id);

        Assert.AreEqual("cancelled", (string)card["status"]!);
        Assert.AreEqual(12, (int)card["pass"]!["remainingVisits"]!);
        await AssertHashAsync(app, user, "1", id);
        Assert.AreEqual(4, Failures(app).Count);
        CollectionAssert.AreEqual(
            new[] { "rate-limit", "cache-remove", "rate-limit", "cache-remove" },
            app.Metrics("club.redis.failures").GetMeasurementSnapshot().Select(x => (string?)x.Tags["operation"]).ToArray());
    }

    [TestMethod]
    public async Task Cancel_PromotesWhileRedisBroken_AuthorAndPromotedSeeResultAfterRestore()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var waiter = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 2);
        var id = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        await EntryAsync(app, waiter.Id, classId, 2);
        var waiterOther = await app.Data.EnrollmentAsync(waiter.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(owner.Email);
        using var waiterClient = await app.CreateClientAsync(waiter.Email);
        Assert.AreEqual("active", (string)(await CardAsync(client, id))["status"]!);
        Assert.AreEqual(10, (int)(await CardAsync(waiterClient, waiterOther))["pass"]!["remainingVisits"]!);
        app.BreakRedis();

        using (var cancel = await CancelAsync(client, id))
        {
            await AssertCancelledAsync(cancel, id, 2);
        }

        AssertFailures(
            app,
            ("rate-limit", typeof(RedisConnectionException)),
            ("cache-remove", typeof(RedisConnectionException)),
            ("cache-remove", typeof(RedisConnectionException)));
        app.RestoreRedis();

        Assert.AreEqual(8, (int)(await CardAsync(waiterClient, waiterOther))["pass"]!["remainingVisits"]!);
        Assert.AreEqual("cancelled", (string)(await CardAsync(client, id))["status"]!);
        await AssertHashAsync(app, owner, "1", id);
        await AssertHashAsync(app, waiter, "1", waiterOther);
        Assert.AreEqual(3, Failures(app).Count);
    }

    [TestMethod]
    public async Task GetCard_KeyOfWrongType_Returns500WithoutRedisFailure()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);
        await app.Redis.StringSetAsync(Key(user), "not a hash");

        using var response = await client.GetAsync($"/enrollments/{id}");

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual(0, Failures(app).Count);
        Assert.AreEqual(0, app.Metrics("club.redis.failures").GetMeasurementSnapshot().Count);
    }

    [TestMethod]
    public async Task GetCard_BrokenSnapshot_Returns500WithoutRedisFailure()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);
        await app.Redis.HashSetAsync(Key(user), [new HashEntry("v", 0), new HashEntry(id, "{not json")]);

        using var response = await client.GetAsync($"/enrollments/{id}");

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual(0, Failures(app).Count);
        Assert.AreEqual(0, app.Metrics("club.redis.failures").GetMeasurementSnapshot().Count);
    }

    [TestMethod]
    public async Task Guard_RequestCancelled_PropagatesCancellationWithoutFailure()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Redis:OperationTimeout", "00:00:04"));
        var guard = app.Services.GetRequiredService<RedisGuard>();
        using var cancellation = new CancellationTokenSource();
        var never = new TaskCompletionSource();

        var call = guard.TryAsync("cache-get", () => never.Task, cancellation.Token);
        Assert.IsFalse(call.IsCompleted);
        await cancellation.CancelAsync();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => call);
        Assert.AreEqual(0, Failures(app).Count);
        Assert.AreEqual(0, app.Metrics("club.redis.failures").GetMeasurementSnapshot().Count);
    }

    [TestMethod]
    [DataRow("00:00:00.001")]
    [DataRow("00:00:04.9999999")]
    public async Task StartAsync_OperationTimeoutOnBoundary_StartsAndServesCard(string value)
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Redis:OperationTimeout", value));
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        Assert.AreEqual(2, (int)(await CardAsync(client, id))["seats"]!);
        Assert.AreEqual(TimeSpan.Parse(value), app.Services.GetRequiredService<IOptions<RedisSettings>>().Value.OperationTimeout);
    }

    [TestMethod]
    [DataRow("00:00:00.0009999")]
    [DataRow("00:00:05")]
    [DataRow("00:00:00")]
    [DataRow("-00:00:01")]
    public async Task StartAsync_OperationTimeoutOutOfRange_FailsWithValidationError(string value)
    {
        var exception = await Assert.ThrowsExceptionAsync<OptionsValidationException>(
            () => TestApp.StartAsync(builder => builder.UseSetting("Redis:OperationTimeout", value)));

        StringAssert.Contains(exception.Message, "Redis:OperationTimeout");
    }

    [TestMethod]
    public async Task Start_DefaultOperationTimeout_IsOneSecond()
    {
        await using var app = await TestApp.StartAsync();

        Assert.AreEqual(TimeSpan.FromSeconds(1), app.Services.GetRequiredService<IOptions<RedisSettings>>().Value.OperationTimeout);
        Assert.AreEqual(0, Failures(app).Count);
    }

    private static List<FakeLogRecord> Failures(TestApp app)
    {
        return app.Logs.GetSnapshot().Where(x => x.Id.Id == 1005).ToList();
    }

    private static void AssertFailures(TestApp app, params (string Operation, Type? Exception)[] expected)
    {
        var records = Failures(app);
        var actual = records.Select(x => (x.GetStructuredStateValue("Operation"), x.Exception?.GetType())).ToArray();

        CollectionAssert.AreEqual(
            expected.Select(x => (x.Operation, x.Exception)).ToArray(),
            actual,
            string.Join("; ", records.Select(x => x.Message)));

        foreach (var record in records)
        {
            Assert.AreEqual(LogLevel.Warning, record.Level);
            Assert.AreEqual("RedisFailure", record.Id.Name);
        }
    }

    private static void AssertFailureMetric(TestApp app, params string[] operations)
    {
        var measurements = app.Metrics("club.redis.failures").GetMeasurementSnapshot();

        CollectionAssert.AreEqual(operations, measurements.Select(x => (string?)x.Tags["operation"]).ToArray());
        Assert.IsTrue(measurements.All(x => x.Value == 1));
    }

    private static async Task AssertHashAsync(TestApp app, TestUser user, string version, params long[] ids)
    {
        var hash = (await app.Redis.HashGetAllAsync(Key(user))).ToDictionary(x => (string)x.Name!, x => (string)x.Value!);

        CollectionAssert.AreEquivalent(
            new[] { "v" }.Concat(ids.Select(x => x.ToString())).ToArray(),
            hash.Keys.ToArray(),
            string.Join(", ", hash));
        Assert.AreEqual(version, hash["v"]);
    }

    private static async Task SetSeatsBypassingApiAsync(TestApp app, long id, int seats)
    {
        await using var context = app.CreateContext();

        await context.Enrollments.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(e => e.Seats, seats));
    }

    private static string Key(TestUser user)
    {
        return $"card:{user.Id}";
    }

    private static async Task<JsonNode> CardAsync(HttpClient client, long id)
    {
        using var response = await client.GetAsync($"/enrollments/{id}");
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);

        return JsonNode.Parse(text)!;
    }

    private sealed class BreakOnFillCardCache(ICardCache inner, KeyPrefixedConnection connection) : ICardCache
    {
        public Task<(bool Available, EnrollmentCard? Card)> GetAsync(long userId, long id, CancellationToken ct)
        {
            return inner.GetAsync(userId, id, ct);
        }

        public Task FillAsync(long userId, VersionedCard card, CancellationToken ct)
        {
            connection.Break();

            return inner.FillAsync(userId, card, ct);
        }

        public Task InvalidateAsync(long userId, long version)
        {
            return inner.InvalidateAsync(userId, version);
        }
    }
}
