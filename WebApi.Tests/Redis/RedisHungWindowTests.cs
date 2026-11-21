using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Redis;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Redis;

[TestClass]
[DoNotParallelize]
public class RedisHungWindowTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task GetCard_RedisHungTwentyRequests_OneTimeoutPerOperation()
    {
        await using var proxy = FreezableRedisProxy.Start(TestDatabase.RedisConnectionString);
        await using var app = await StartAsync(proxy);
        var (client, _, id) = await CardClientAsync(app);
        using var owned = client;
        proxy.Freeze();

        for (var i = 0; i < 20; i++)
        {
            Assert.AreEqual(2, (int)(await CardAsync(client, id))["seats"]!);
        }

        AssertFailures(app, "rate-limit", "cache-get");
        foreach (var failure in Failures(app))
        {
            Assert.IsInstanceOfType<TimeoutException>(failure.Exception);
        }

        AssertFailureMetric(app, "rate-limit", "cache-get");
    }

    [TestMethod]
    public async Task GetCard_RedisHungWindowBoundary_SkipsBeforeAndProbesOnceAtExpiry()
    {
        await using var proxy = FreezableRedisProxy.Start(TestDatabase.RedisConnectionString);
        await using var app = await StartAsync(proxy);
        var (client, _, id) = await CardClientAsync(app);
        using var owned = client;
        proxy.Freeze();
        await CardAsync(client, id);
        AssertFailures(app, "rate-limit", "cache-get");

        app.Clock.Advance(Window - TimeSpan.FromTicks(1));
        await CardAsync(client, id);

        AssertFailures(app, "rate-limit", "cache-get");

        app.Clock.Advance(TimeSpan.FromTicks(1));
        await CardAsync(client, id);

        AssertFailures(app, "rate-limit", "cache-get", "rate-limit", "cache-get");
        AssertFailureMetric(app, "rate-limit", "cache-get", "rate-limit", "cache-get");

        app.Clock.Advance(Window - TimeSpan.FromTicks(1));
        await CardAsync(client, id);

        AssertFailures(app, "rate-limit", "cache-get", "rate-limit", "cache-get");
    }

    [TestMethod]
    public async Task GetCard_ConcurrentRequestsAtWindowExpiry_OneProbePerOperation()
    {
        await using var proxy = FreezableRedisProxy.Start(TestDatabase.RedisConnectionString);
        await using var app = await StartAsync(proxy);
        var (client, _, id) = await CardClientAsync(app);
        using var owned = client;
        proxy.Freeze();
        await CardAsync(client, id);
        app.Clock.Advance(Window);

        var cards = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => CardAsync(client, id)));

        Assert.IsTrue(cards.All(x => (int)x["seats"]! == 2));
        CollectionAssert.AreEquivalent(
            new[] { "rate-limit", "cache-get", "rate-limit", "cache-get" },
            Failures(app).Select(x => x.GetStructuredStateValue("Operation")).ToArray());
        Assert.AreEqual(4, app.Metrics("club.redis.failures").GetMeasurementSnapshot().Count);
    }

    [TestMethod]
    public async Task GetCard_RedisRecoveredAfterWindow_ProbeSucceedsAndCacheIsFilledAgain()
    {
        await using var proxy = FreezableRedisProxy.Start(TestDatabase.RedisConnectionString);
        await using var app = await StartAsync(proxy);
        var (client, userId, id) = await CardClientAsync(app);
        using var owned = client;
        proxy.Freeze();
        await CardAsync(client, id);
        AssertFailures(app, "rate-limit", "cache-get");

        proxy.Unfreeze();
        app.Clock.Advance(Window);
        Assert.AreEqual(2, (int)(await CardAsync(client, id))["seats"]!);

        Assert.IsTrue(await app.Redis.HashExistsAsync($"card:{userId}", id.ToString()));
        await app.Redis.KeyDeleteAsync($"card:{userId}");
        await CardAsync(client, id);
        Assert.IsTrue(await app.Redis.HashExistsAsync($"card:{userId}", id.ToString()));
        AssertFailures(app, "rate-limit", "cache-get");
        AssertFailureMetric(app, "rate-limit", "cache-get");
    }

    [TestMethod]
    public async Task HealthReady_RedisHungAndWindowOpen_ReturnsDegraded()
    {
        await using var proxy = FreezableRedisProxy.Start(TestDatabase.RedisConnectionString);
        await using var app = await StartAsync(proxy);
        var (client, _, id) = await CardClientAsync(app);
        using var owned = client;
        proxy.Freeze();
        await CardAsync(client, id);

        using var response = await client.GetAsync("/health/ready");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Degraded", await response.Content.ReadAsStringAsync());
    }

    private static Task<TestApp> StartAsync(FreezableRedisProxy proxy)
    {
        return TestApp.StartAsync(builder => builder
            .UseSetting("Redis:OperationTimeout", "00:00:00.200")
            .ConfigureTestServices(services => services.AddSingleton(_ => KeyPrefixedConnection.Wrap(
                RedisConnection.Connect(proxy.ConnectionString),
                $"test:{Guid.NewGuid():N}:"))));
    }

    private static async Task<(HttpClient Client, long UserId, long Id)> CardClientAsync(TestApp app)
    {
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);

        return (await app.CreateClientAsync(user.Email), user.Id, id);
    }

    private static async Task<JsonNode> CardAsync(HttpClient client, long id)
    {
        using var response = await client.GetAsync($"/enrollments/{id}");
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);

        return JsonNode.Parse(text)!;
    }

    private static List<FakeLogRecord> Failures(TestApp app)
    {
        return app.Logs.GetSnapshot().Where(x => x.Id.Id == 1005).ToList();
    }

    private static void AssertFailures(TestApp app, params string[] operations)
    {
        var records = Failures(app);

        CollectionAssert.AreEqual(
            operations,
            records.Select(x => x.GetStructuredStateValue("Operation")).ToArray(),
            string.Join("; ", records.Select(x => x.Message)));
    }

    private static void AssertFailureMetric(TestApp app, params string[] operations)
    {
        var measurements = app.Metrics("club.redis.failures").GetMeasurementSnapshot();

        CollectionAssert.AreEqual(operations, measurements.Select(x => (string?)x.Tags["operation"]).ToArray());
        Assert.IsTrue(measurements.All(x => x.Value == 1));
    }
}
