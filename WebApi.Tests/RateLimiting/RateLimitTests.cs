using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using StackExchange.Redis;
using WebApi.Tests.Analytics;
using WebApi.Tests.Infrastructure;
using WebApi.Tests.Schedule;
using WebApi.Tests.Waitlist;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.RateLimiting;

[TestClass]
public class RateLimitTests
{
    private const string MissingCard = "/enrollments/999999999";

    private static readonly int[] SuccessEvents = [1001, 1004, 2001, 2002, 2003, 2004, 2005, 3001, 3002, 3003, 3004];

    private static readonly string[] SuccessInstruments =
    [
        "club.enrollments.created",
        "club.visits.charged",
        "club.visits.refunded",
        "club.idempotency.replayed",
        "club.waitlist.joined",
        "club.waitlist.left",
        "club.waitlist.promoted"
    ];

    [TestMethod]
    public async Task RateLimit_Journal_IsUnderAppKeyPrefixAndHoldsOnlyAcceptedRequests()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await using var raw = await ConnectionMultiplexer.ConnectAsync(TestDatabase.RedisConnectionString);

        await BurnAsync(client, 60);
        using var rejected = await client.GetAsync(MissingCard);

        await AssertTooManyAsync(rejected, "60");
        Assert.AreEqual(60, await raw.GetDatabase().SortedSetLengthAsync($"{app.RedisKeyPrefix}rate:{user.Id}"));
        Assert.IsFalse(await raw.GetDatabase().KeyExistsAsync($"rate:{user.Id}"));
    }

    [TestMethod]
    public async Task RateLimit_60RequestsInOneMillisecond_AllPassAnd61stReturns429()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);

        for (var i = 0; i < 60; i++)
        {
            using var response = await ScheduleApi.GetAsync(client, Now(app), Now(app).AddDays(1));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"request {i + 1}");
        }

        using var rejected = await ScheduleApi.GetAsync(client, Now(app), Now(app).AddDays(1));

        await AssertTooManyAsync(rejected, "60");
    }

    [TestMethod]
    public async Task RateLimit_WindowBoundary_RejectsAt59999MsAndAcceptsAt60000Ms()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await BurnAsync(client, 60);

        app.Clock.Advance(TimeSpan.FromMilliseconds(59_999));
        using (var rejected = await client.GetAsync(MissingCard))
        {
            await AssertTooManyAsync(rejected, "1");
        }

        app.Clock.Advance(TimeSpan.FromMilliseconds(1));
        using var accepted = await client.GetAsync(MissingCard);

        Assert.AreEqual(HttpStatusCode.NotFound, accepted.StatusCode);
    }

    [TestMethod]
    public async Task RateLimit_SeriesAcrossClockMinute_61stReturns429()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var ms = app.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        app.Clock.Advance(TimeSpan.FromMilliseconds((ms / 60_000 + 1) * 60_000 - 500 - ms));

        await BurnAsync(client, 30);
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        await BurnAsync(client, 30);
        using var rejected = await client.GetAsync(MissingCard);

        await AssertTooManyAsync(rejected, "59");
    }

    [TestMethod]
    public async Task RateLimit_RetryAfter_IsSecondsUntilOldestLeavesWindowRoundedUp()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var start = app.Clock.GetUtcNow();
        await BurnAsync(client, 1);
        app.Clock.Advance(TimeSpan.FromMilliseconds(1500));
        await BurnAsync(client, 59);

        (int Ms, string RetryAfter)[] cases = [(1500, "59"), (1501, "59"), (2000, "58"), (2001, "58"), (59_000, "1"), (59_999, "1")];
        foreach (var (at, retryAfter) in cases)
        {
            app.Clock.SetUtcNow(start.AddMilliseconds(at));
            using var rejected = await client.GetAsync(MissingCard);
            await AssertTooManyAsync(rejected, retryAfter);
        }

        app.Clock.SetUtcNow(start.AddMilliseconds(60_000));
        await BurnAsync(client, 1);
    }

    [TestMethod]
    public async Task RateLimit_RejectedRequests_AreNotJournaled()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await BurnAsync(client, 60);

        app.Clock.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 60; i++)
        {
            using var rejected = await client.GetAsync(MissingCard);
            await AssertTooManyAsync(rejected, "30");
        }

        app.Clock.Advance(TimeSpan.FromSeconds(30));
        await BurnAsync(client, 60);
    }

    [TestMethod]
    public async Task RateLimit_DifferentUsers_HaveSeparateCounters()
    {
        await using var app = await TestApp.StartAsync();
        var first = await app.Data.UserAsync();
        var second = await app.Data.UserAsync();
        using var firstClient = await app.CreateClientAsync(first.Email);
        using var secondClient = await app.CreateClientAsync(second.Email);
        await BurnAsync(firstClient, 60);

        await BurnAsync(secondClient, 60);
        using var firstRejected = await firstClient.GetAsync(MissingCard);
        using var secondRejected = await secondClient.GetAsync(MissingCard);

        await AssertTooManyAsync(firstRejected, "60");
        await AssertTooManyAsync(secondRejected, "60");
    }

    [TestMethod]
    public async Task RateLimit_AnonymousRequests_AreNotCountedAndGet401()
    {
        await using var app = await TestApp.StartAsync();
        using var client = app.CreateClient();
        await using var raw = await ConnectionMultiplexer.ConnectAsync(TestDatabase.RedisConnectionString);

        for (var i = 0; i < 61; i++)
        {
            using var response = await client.GetAsync(MissingCard);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"request {i + 1}");
        }

        var server = raw.GetServer(raw.GetEndPoints().Single());
        var keys = server.Keys(raw.GetDatabase().Database, $"{app.RedisKeyPrefix}rate:*").ToList();
        Assert.AreEqual(0, keys.Count);
        Assert.AreEqual(0, app.Metrics("club.requests.rejected").GetMeasurementSnapshot().Count);
    }

    [TestMethod]
    public async Task RateLimit_EndpointsOutsideLimit_AreNotCounted()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        (string Name, Func<Task<HttpResponseMessage>> Send, HttpStatusCode Status)[] outside =
        [
            ("/health", () => client.GetAsync("/health"), HttpStatusCode.OK),
            ("/health/ready", () => client.GetAsync("/health/ready"), HttpStatusCode.OK),
            ("/auth/login", () => client.LoginAsync(user.Email), HttpStatusCode.OK),
            ("swagger", () => client.GetAsync("/swagger/v1/swagger.json"), HttpStatusCode.OK),
            ("/harness/me", () => client.GetAsync("/harness/me"), HttpStatusCode.OK),
            ("missing route", () => client.GetAsync("/no-such-route"), HttpStatusCode.NotFound),
            ("405", () => client.DeleteAsync("/schedule"), HttpStatusCode.MethodNotAllowed)
        ];

        foreach (var (name, send, status) in outside)
        {
            for (var i = 0; i < 61; i++)
            {
                using var response = await send();
                Assert.AreEqual(status, response.StatusCode, $"{name}, request {i + 1}");
            }
        }

        await BurnAsync(client, 60);
        using var rejected = await client.GetAsync(MissingCard);
        await AssertTooManyAsync(rejected, "60");
    }

    [TestMethod]
    [DataRow("400", "/schedule", HttpStatusCode.BadRequest)]
    [DataRow("403", "/analytics/load?from=2026-01-01&to=2026-01-02", HttpStatusCode.Forbidden)]
    [DataRow("404", MissingCard, HttpStatusCode.NotFound)]
    public async Task RateLimit_RefusedRequests_AreCounted(string name, string path, HttpStatusCode status)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);

        for (var i = 0; i < 60; i++)
        {
            using var response = await client.GetAsync(path);
            Assert.AreEqual(status, response.StatusCode, $"{name}, request {i + 1}");
        }

        using var rejected = await client.GetAsync(path);
        await AssertTooManyAsync(rejected, "60");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task RateLimit_70ConcurrentRequests_Exactly60Pass()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);

        var responses = await Task.WhenAll(Enumerable.Range(0, 70).Select(_ => client.GetAsync(MissingCard)));

        try
        {
            Assert.AreEqual(60, responses.Count(x => x.StatusCode == HttpStatusCode.NotFound));
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

    [TestMethod]
    public async Task RateLimit_Rejection_LogsEventAndCountsRejection()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await BurnAsync(client, 60);
        app.Clock.Advance(TimeSpan.FromSeconds(12.5));

        using var rejected = await client.GetAsync(MissingCard);

        await AssertTooManyAsync(rejected, "48");
        using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        var traceId = body.RootElement.GetProperty("traceId").GetString()!.Split('-')[1];
        var record = app.Logs.GetSnapshot().Single(x => x.Id.Id == 1003);
        Assert.AreEqual("RequestRateLimited", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        Assert.AreEqual("48", record.GetStructuredStateValue("RetryAfter"));
        Assert.AreEqual(traceId, ScopeValue(record, "TraceId"));
        var measurement = app.Metrics("club.requests.rejected").GetMeasurementSnapshot().Single();
        Assert.AreEqual(1, measurement.Value);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, object?> { ["operation"] = "rate-limit", ["reason"] = "too-many-requests" },
            measurement.Tags.ToDictionary(x => x.Key, x => x.Value));
    }

    [TestMethod]
    public async Task RateLimit_61stWithBrokenBody_Returns429Not400()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await BurnAsync(client, 60);

        using var rejected = await PostAsync(client, "{");

        await AssertTooManyAsync(rejected, "60");
    }

    [TestMethod]
    public async Task RateLimit_61stFromNonAdminOnRoomLoad_Returns429Not403()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await BurnAsync(client, 60);

        using var rejected = await RoomLoadApi.GetAsync(client, "from=2026-01-01&to=2026-01-02");

        await AssertTooManyAsync(rejected, "60");
    }

    [TestMethod]
    [DataRow("POST /enrollments", HttpStatusCode.Created)]
    [DataRow("POST /enrollments/{id}/cancel", HttpStatusCode.OK)]
    [DataRow("POST /enrollments/{id}/seats", HttpStatusCode.OK)]
    [DataRow("GET /enrollments/{id}", HttpStatusCode.OK)]
    [DataRow("GET /schedule", HttpStatusCode.OK)]
    [DataRow("GET /analytics/load", HttpStatusCode.OK)]
    [DataRow("POST /enrollments/waitlist", HttpStatusCode.Created)]
    [DataRow("POST /enrollments/waitlist/{id}/cancel", HttpStatusCode.OK)]
    public async Task RateLimit_61stOnEndpoint_Returns429WithoutTrace(string endpoint, HttpStatusCode acceptedStatus)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var enrollClassId = await app.Data.ClassAsync();
        var classId = await app.Data.ClassAsync();
        var enrollmentId = await app.Data.EnrollmentAsync(user.Id, classId, 1);
        var leaveClassId = await WaitlistApi.FullClassAsync(app);
        var entryId = await WaitlistApi.EntryAsync(app, user.Id, leaveClassId, 1);
        var joinClassId = await WaitlistApi.FullClassAsync(app);
        using var client = await app.CreateClientAsync(endpoint == "GET /analytics/load" ? AuthClient.SeededAdmin : user.Email);
        var key = Guid.NewGuid().ToString();
        var now = Now(app);
        Func<Task<HttpResponseMessage>> send = endpoint switch
        {
            "POST /enrollments" => () => PostAsync(client, $"{{\"classId\":{enrollClassId},\"seats\":1}}", key),
            "POST /enrollments/{id}/cancel" => () => client.PostAsync($"/enrollments/{enrollmentId}/cancel", null),
            "POST /enrollments/{id}/seats" => () => client.PostAsync(
                $"/enrollments/{enrollmentId}/seats",
                new StringContent("{\"seats\":2}", Encoding.UTF8, "application/json")),
            "GET /enrollments/{id}" => () => client.GetAsync($"/enrollments/{enrollmentId}"),
            "GET /schedule" => () => ScheduleApi.GetAsync(client, now, now.AddDays(2)),
            "GET /analytics/load" => () => RoomLoadApi.GetAsync(client, now, now.AddDays(2)),
            "POST /enrollments/waitlist" => () => WaitlistPostAsync(client, $"{{\"classId\":{joinClassId},\"seats\":1}}", key),
            "POST /enrollments/waitlist/{id}/cancel" => () => client.PostAsync($"/enrollments/waitlist/{entryId}/cancel", null),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };
        var before = await StateAsync(app, user, enrollClassId, classId, leaveClassId, joinClassId);
        await BurnAsync(client, 60);

        using (var rejected = await send())
        {
            await AssertTooManyAsync(rejected, "60");
        }

        Assert.AreEqual(before, await StateAsync(app, user, enrollClassId, classId, leaveClassId, joinClassId));
        Assert.IsFalse(app.Logs.GetSnapshot().Any(x => SuccessEvents.Contains(x.Id.Id)));
        foreach (var instrument in SuccessInstruments)
        {
            Assert.AreEqual(0, app.Metrics(instrument).GetMeasurementSnapshot().Count, instrument);
        }

        Assert.AreEqual(1, app.Metrics("club.requests.rejected").GetMeasurementSnapshot().Count);

        app.Clock.Advance(TimeSpan.FromSeconds(60));
        using var accepted = await send();
        Assert.AreEqual(acceptedStatus, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> WaitlistPostAsync(HttpClient client, string json, string key)
    {
        return WaitlistApi.PostAsync(client, json, key);
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }

    private static async Task BurnAsync(HttpClient client, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var response = await client.GetAsync(MissingCard);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, $"burn request {i + 1}");
        }
    }

    private static async Task AssertTooManyAsync(HttpResponseMessage response, string retryAfter)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode, text);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(retryAfter, response.Headers.GetValues("Retry-After").Single());
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        CollectionAssert.AreEqual(
            new[] { "status", "title", "traceId", "type" },
            root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.AreEqual(429, root.GetProperty("status").GetInt32());
        Assert.AreEqual("Too Many Requests", root.GetProperty("title").GetString());
        Assert.AreEqual("https://tools.ietf.org/html/rfc6585#section-4", root.GetProperty("type").GetString());
        StringAssert.Matches(root.GetProperty("traceId").GetString(), new Regex(TraceParent));
    }

    private static async Task<string> StateAsync(TestApp app, TestUser user, params long[] classIds)
    {
        var parts = new List<string> { (await RemainingVisitsAsync(app, user)).ToString() };
        foreach (var classId in classIds)
        {
            parts.AddRange(await EnrollmentsSnapshotAsync(app, classId));
            parts.AddRange(await WaitlistApi.WaitlistSnapshotAsync(app, classId));
        }

        await using var context = app.CreateContext();
        parts.Add((await context.IdempotencyKeys.CountAsync(x => x.UserId == user.Id)).ToString());

        return string.Join("\n", parts);
    }

    private static string? ScopeValue(FakeLogRecord record, string key)
    {
        return record.Scopes
            .OfType<IEnumerable<KeyValuePair<string, object?>>>()
            .SelectMany(x => x)
            .Where(x => x.Key == key)
            .Select(x => x.Value?.ToString())
            .FirstOrDefault();
    }
}
