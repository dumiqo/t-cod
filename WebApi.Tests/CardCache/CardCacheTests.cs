using System.Net;
using System.Text.Json.Nodes;
using DataAccessLayer.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebApi.CardCache;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.ChangeSeatsApi;
using static WebApi.Tests.Enrollments.EnrollApi;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.CardCache;

[TestClass]
public class CardCacheTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ScriptEvaluate_PrefixedConnection_PrefixesKeys()
    {
        await using var app = await TestApp.StartAsync();
        await using var raw = await ConnectionMultiplexer.ConnectAsync(TestDatabase.RedisConnectionString);

        await app.Redis.ScriptEvaluateAsync("return redis.call('SET', KEYS[1], ARGV[1])", ["probe"], ["x"]);

        Assert.AreEqual("x", (string?)await raw.GetDatabase().StringGetAsync(app.RedisKeyPrefix + "probe"));
        Assert.IsFalse(await raw.GetDatabase().KeyExistsAsync("probe"));
    }

    [TestMethod]
    public async Task GetCard_Miss_FillsHashWithVersionAndSnapshotUnderDefaultTtl()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.GetAsync($"/enrollments/{id}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var hash = await HashAsync(app, user);
        CollectionAssert.AreEquivalent(new[] { "v", id.ToString() }, hash.Keys.ToArray());
        Assert.AreEqual("0", hash["v"]);
        var snapshot = JsonNode.Parse(hash[id.ToString()])!;
        Assert.AreEqual(id, (long)snapshot["Id"]!);
        Assert.AreEqual(2, (int)snapshot["Seats"]!);
        await AssertTtlAsync(app, user, TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task GetCard_Hit_BodyEqualsMissBodyByteForByte()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(validUntil: new DateOnly(2026, 2, 28));
        var classId = await app.Data.ClassAsync(startsAt: app.Clock.GetUtcNow().UtcDateTime.AddDays(1).AddTicks(1234567));
        app.Clock.Advance(TimeSpan.FromTicks(7654321));
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        var miss = await (await client.GetAsync($"/enrollments/{id}")).Content.ReadAsByteArrayAsync();
        Assert.IsTrue(await app.Redis.HashExistsAsync(Key(user), id.ToString()));
        var hit = await (await client.GetAsync($"/enrollments/{id}")).Content.ReadAsByteArrayAsync();

        CollectionAssert.AreEqual(miss, hit, $"miss {System.Text.Encoding.UTF8.GetString(miss)}, hit {System.Text.Encoding.UTF8.GetString(hit)}");
    }

    [TestMethod]
    public async Task GetCard_ChangedInDbBypassingApi_ReturnsCachedCard()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);
        Assert.AreEqual(2, await SeatsAsync(client, id));

        await using (var context = app.CreateContext())
        {
            await context.Enrollments.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(e => e.Seats, 5));
        }

        Assert.AreEqual(2, await SeatsAsync(client, id));
    }

    [TestMethod]
    public async Task GetCard_SameUserSecondCard_AddsFieldToSameVersion()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var first = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        var second = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        Assert.AreEqual(1, await SeatsAsync(client, first));
        Assert.AreEqual(2, await SeatsAsync(client, second));

        var hash = await HashAsync(app, user);
        CollectionAssert.AreEquivalent(new[] { "v", first.ToString(), second.ToString() }, hash.Keys.ToArray());
        Assert.AreEqual("0", hash["v"]);
    }

    [TestMethod]
    public async Task GetCard_HashVersionNewerThanDb_ReturnsDbCardAndWritesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);
        await app.Redis.HashSetAsync(Key(user), "v", 5);

        Assert.AreEqual(2, await SeatsAsync(client, id));

        var hash = await HashAsync(app, user);
        CollectionAssert.AreEquivalent(new[] { "v" }, hash.Keys.ToArray());
        Assert.AreEqual("5", hash["v"]);
    }

    [TestMethod]
    public async Task GetCard_HashVersionOlderThanDb_ReplacesHash()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);
        await using (var context = app.CreateContext())
        {
            await context.Passes.Where(x => x.UserId == user.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Version, 3L));
        }

        await app.Redis.HashSetAsync(Key(user), [new HashEntry("v", 2), new HashEntry("999", "stale")]);

        Assert.AreEqual(2, await SeatsAsync(client, id));

        var hash = await HashAsync(app, user);
        CollectionAssert.AreEquivalent(new[] { "v", id.ToString() }, hash.Keys.ToArray());
        Assert.AreEqual("3", hash["v"]);
    }

    [TestMethod]
    public async Task GetCard_OtherUsersCachedCard_Returns404AndCachesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var stranger = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 2);
        using var ownerClient = await app.CreateClientAsync(owner.Email);
        using var strangerClient = await app.CreateClientAsync(stranger.Email);
        Assert.AreEqual(2, await SeatsAsync(ownerClient, id));

        using var response = await strangerClient.GetAsync($"/enrollments/{id}");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.IsFalse(await app.Redis.KeyExistsAsync(Key(stranger)));
    }

    [TestMethod]
    public async Task GetCard_ByEntryIdBeforeAndAfterPromotion_404NotCachedThenEnrollment()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var waiter = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 2);
        var id = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        var entryId = await EntryAsync(app, waiter.Id, classId, 2);
        using var ownerClient = await app.CreateClientAsync(owner.Email);
        using var waiterClient = await app.CreateClientAsync(waiter.Email);

        using (var before = await waiterClient.GetAsync($"/enrollments/{entryId}"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, before.StatusCode);
        }

        Assert.IsFalse(await app.Redis.KeyExistsAsync(Key(waiter)));
        using var cancel = await CancelAsync(ownerClient, id);
        await AssertCancelledAsync(cancel, id, 2);

        var card = await CardAsync(waiterClient, entryId);
        Assert.AreEqual("active", (string)card["status"]!);
        Assert.AreEqual(2, (int)card["seats"]!);
        Assert.AreEqual(8, (int)card["pass"]!["remainingVisits"]!);
    }

    [TestMethod]
    public async Task Enroll_Created_TombstonesAuthorAndOtherCardSeesRemaining()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Cache:CardTtl", "00:00:30"));
        var user = await app.Data.UserAsync();
        var bystander = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        var other = await app.Data.EnrollmentAsync(bystander.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);
        using var bystanderClient = await app.CreateClientAsync(bystander.Email);
        Assert.AreEqual(10, await RemainingAsync(client, id));
        Assert.AreEqual(10, await RemainingAsync(bystanderClient, other));
        await app.Redis.KeyExpireAsync(Key(user), TimeSpan.FromSeconds(2));

        using var response = await EnrollAsync(client, await app.Data.ClassAsync(), 2);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        var hash = await HashAsync(app, user);
        CollectionAssert.AreEquivalent(new[] { "v" }, hash.Keys.ToArray());
        Assert.AreEqual("1", hash["v"]);
        await AssertTtlAsync(app, user, TimeSpan.FromSeconds(30));
        Assert.IsTrue(await app.Redis.HashExistsAsync(Key(bystander), other.ToString()));
        Assert.AreEqual(8, await RemainingAsync(client, id));
    }

    [TestMethod]
    public async Task Enroll_Rejected_KeepsCachedCard()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 1);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);
        Assert.AreEqual(1, await RemainingAsync(client, id));

        using var response = await EnrollAsync(client, await app.Data.ClassAsync(), 2);

        await AssertConflictAsync(response, "pass-exhausted");
        var hash = await HashAsync(app, user);
        CollectionAssert.AreEquivalent(new[] { "v", id.ToString() }, hash.Keys.ToArray());
        Assert.AreEqual("0", hash["v"]);
    }

    [TestMethod]
    public async Task ChangeSeats_Changed_NextCardsSeeSeatsAndRemaining()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 3);
        var other = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);
        Assert.AreEqual(3, await SeatsAsync(client, id));
        Assert.AreEqual(10, await RemainingAsync(client, other));

        using var response = await ChangeSeatsAsync(client, id, 4);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(4, await SeatsAsync(client, id));
        Assert.AreEqual(9, await RemainingAsync(client, other));
        Assert.AreEqual("1", (await HashAsync(app, user))["v"]);
    }

    [TestMethod]
    public async Task ChangeSeats_SameSeats_KeepsCachedCard()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 3);
        using var client = await app.CreateClientAsync(user.Email);
        Assert.AreEqual(3, await SeatsAsync(client, id));

        using var response = await ChangeSeatsAsync(client, id, 3);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var hash = await HashAsync(app, user);
        CollectionAssert.AreEquivalent(new[] { "v", id.ToString() }, hash.Keys.ToArray());
        Assert.AreEqual("0", hash["v"]);
    }

    [TestMethod]
    public async Task ChangeSeats_DecreasePromotes_PromotedCardSeesRemaining()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var waiter = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 3);
        var id = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        await EntryAsync(app, waiter.Id, classId, 2);
        var waiterOther = await app.Data.EnrollmentAsync(waiter.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(owner.Email);
        using var waiterClient = await app.CreateClientAsync(waiter.Email);
        Assert.AreEqual(10, await RemainingAsync(waiterClient, waiterOther));

        using var response = await ChangeSeatsAsync(client, id, 1);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(8, await RemainingAsync(waiterClient, waiterOther));
    }

    [TestMethod]
    public async Task Cancel_Cancelled_NextCardShowsCancelledAndRefund()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);
        Assert.AreEqual("active", (string)(await CardAsync(client, id))["status"]!);

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 2);
        var card = await CardAsync(client, id);
        Assert.AreEqual("cancelled", (string)card["status"]!);
        Assert.AreEqual(12, (int)card["pass"]!["remainingVisits"]!);
    }

    [TestMethod]
    public async Task Cancel_Promotes_PromotedOtherCardSeesRemaining()
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
        Assert.AreEqual(10, await RemainingAsync(waiterClient, waiterOther));

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 2);
        Assert.AreEqual(8, await RemainingAsync(waiterClient, waiterOther));
        Assert.AreEqual("1", (await HashAsync(app, waiter))["v"]);
    }

    [TestMethod]
    public async Task GetCard_Fill_ExtendsTtlToSetting()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Cache:CardTtl", "00:00:30"));
        var user = await app.Data.UserAsync();
        var first = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        var second = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);
        Assert.AreEqual(1, await SeatsAsync(client, first));
        await AssertTtlAsync(app, user, TimeSpan.FromSeconds(30));
        await app.Redis.KeyExpireAsync(Key(user), TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, await SeatsAsync(client, second));

        await AssertTtlAsync(app, user, TimeSpan.FromSeconds(30));
    }

    [TestMethod]
    [DataRow("00:00:01")]
    [DataRow("01:00:00")]
    public async Task StartAsync_CardTtlOnBoundary_StartsAndUsesIt(string value)
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Cache:CardTtl", value));
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);

        Assert.AreEqual(1, await SeatsAsync(client, id));

        var ttl = await app.Redis.KeyTimeToLiveAsync(Key(user));
        Assert.IsNotNull(ttl);
        Assert.IsTrue(ttl <= TimeSpan.Parse(value) && ttl > TimeSpan.Parse(value) - TimeSpan.FromSeconds(1), $"ttl {ttl}");
    }

    [TestMethod]
    [DataRow("00:00:00.999")]
    [DataRow("01:00:00.001")]
    [DataRow("00:00:00")]
    [DataRow("-00:00:01")]
    public async Task StartAsync_CardTtlOutOfRange_FailsWithValidationError(string value)
    {
        var exception = await Assert.ThrowsExceptionAsync<OptionsValidationException>(
            () => TestApp.StartAsync(builder => builder.UseSetting("Cache:CardTtl", value)));

        StringAssert.Contains(exception.Message, "Cache:CardTtl");
    }

    [TestMethod]
    public async Task GetCard_FillDelayedPastCancel_FillWritesNothingAndNextCardIsCancelled()
    {
        var gate = new FillGate();
        await using var app = await StartGatedAsync(gate);
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        var read = CardAsync(client, id);
        await gate.Entered.Task.WaitAsync(Wait);
        using (var cancel = await CancelAsync(client, id))
        {
            await AssertCancelledAsync(cancel, id, 2);
        }

        await AssertTombstoneAsync(app, user, "1");
        gate.Release.SetResult();

        Assert.AreEqual("active", (string)(await read.WaitAsync(Wait))["status"]!);
        await AssertTombstoneAsync(app, user, "1");
        var card = await CardAsync(client, id);
        Assert.AreEqual("cancelled", (string)card["status"]!);
        Assert.AreEqual(12, (int)card["pass"]!["remainingVisits"]!);
    }

    [TestMethod]
    public async Task GetCard_FillDelayedPastPromotion_FillWritesNothingAndNextCardSeesRemaining()
    {
        var gate = new FillGate();
        await using var app = await StartGatedAsync(gate);
        var owner = await app.Data.UserAsync();
        var waiter = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 2);
        var id = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        await EntryAsync(app, waiter.Id, classId, 2);
        var waiterOther = await app.Data.EnrollmentAsync(waiter.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(owner.Email);
        using var waiterClient = await app.CreateClientAsync(waiter.Email);

        var read = CardAsync(waiterClient, waiterOther);
        await gate.Entered.Task.WaitAsync(Wait);
        using (var cancel = await CancelAsync(client, id))
        {
            await AssertCancelledAsync(cancel, id, 2);
        }

        await AssertTombstoneAsync(app, waiter, "1");
        gate.Release.SetResult();

        Assert.AreEqual(10, (int)(await read.WaitAsync(Wait))["pass"]!["remainingVisits"]!);
        await AssertTombstoneAsync(app, waiter, "1");
        Assert.AreEqual(8, await RemainingAsync(waiterClient, waiterOther));
    }

    private static Task<TestApp> StartGatedAsync(FillGate gate)
    {
        return TestApp.StartAsync(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ICardCache>(provider =>
                new GatedCardCache(ActivatorUtilities.CreateInstance<WebApi.CardCache.CardCache>(provider), gate))));
    }

    private static string Key(TestUser user)
    {
        return $"card:{user.Id}";
    }

    private static async Task<Dictionary<string, string>> HashAsync(TestApp app, TestUser user)
    {
        var entries = await app.Redis.HashGetAllAsync(Key(user));

        return entries.ToDictionary(x => (string)x.Name!, x => (string)x.Value!);
    }

    private static async Task AssertTombstoneAsync(TestApp app, TestUser user, string version)
    {
        var hash = await HashAsync(app, user);

        CollectionAssert.AreEquivalent(new[] { "v" }, hash.Keys.ToArray(), string.Join(", ", hash));
        Assert.AreEqual(version, hash["v"]);
    }

    private static async Task AssertTtlAsync(TestApp app, TestUser user, TimeSpan setting)
    {
        var ttl = await app.Redis.KeyTimeToLiveAsync(Key(user));

        Assert.IsNotNull(ttl);
        Assert.IsTrue(ttl <= setting && ttl > setting - TimeSpan.FromSeconds(5), $"ttl {ttl}, setting {setting}");
    }

    private static async Task<JsonNode> CardAsync(HttpClient client, long id)
    {
        using var response = await client.GetAsync($"/enrollments/{id}");
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);

        return JsonNode.Parse(text)!;
    }

    private static async Task<int> SeatsAsync(HttpClient client, long id)
    {
        return (int)(await CardAsync(client, id))["seats"]!;
    }

    private static async Task<int> RemainingAsync(HttpClient client, long id)
    {
        return (int)(await CardAsync(client, id))["pass"]!["remainingVisits"]!;
    }

    private sealed class FillGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedCardCache(ICardCache inner, FillGate gate) : ICardCache
    {
        public Task<(bool Available, EnrollmentCard? Card)> GetAsync(long userId, long id, CancellationToken ct)
        {
            return inner.GetAsync(userId, id, ct);
        }

        public async Task FillAsync(long userId, VersionedCard card, CancellationToken ct)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task;
            await inner.FillAsync(userId, card, ct);
        }

        public Task InvalidateAsync(long userId, long version)
        {
            return inner.InvalidateAsync(userId, version);
        }
    }
}
