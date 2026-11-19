using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class EnrollConcurrencyTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task Enroll_FiveUsersRaceForLastSeat_OneCreatedOthersCapacityExceeded()
    {
        await using var app = await TestApp.StartAsync();
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 2);
        await app.Data.EnrollmentAsync(other.Id, classId, 1);
        var users = new List<TestUser>();
        var clients = new List<HttpClient>();
        for (var i = 0; i < 5; i++)
        {
            var user = await app.Data.UserAsync(remainingVisits: 10);
            users.Add(user);
            clients.Add(await app.CreateClientAsync(user.Email));
        }

        await using var held = await HoldClassAsync(app, classId);
        var requests = clients.Select(x => EnrollAsync(x, classId, 1)).ToArray();
        var blocked = await held.WaitForBlockedCountAsync(5);
        await held.DisposeAsync();
        var responses = await Task.WhenAll(requests);

        Assert.AreEqual(5L, blocked);
        Assert.AreEqual(1, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
        foreach (var response in responses.Where(x => x.StatusCode != HttpStatusCode.Created))
        {
            await AssertConflictAsync(response, "capacity-exceeded");
        }

        await using var context = app.CreateContext();
        var enrollments = await context.Enrollments.Where(x => x.ClassId == classId).ToListAsync();
        Assert.AreEqual(2, enrollments.Count);
        Assert.AreEqual(2, enrollments.Where(x => x.Status == "active").Sum(x => x.Seats));
        var winner = enrollments.Single(x => x.UserId != other.Id).UserId;
        foreach (var user in users)
        {
            Assert.AreEqual(user.Id == winner ? 9 : 10, await RemainingVisitsAsync(app, user));
        }

        DisposeAll(responses, clients);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Enroll_LastVisitRaceOnTwoClasses_OneCreatedOtherPassExhausted()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 1);
        var first = await app.Data.ClassAsync();
        var second = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldPassAsync(app, user);
        var requests = new[] { EnrollAsync(client, first, 1), EnrollAsync(client, second, 1) };
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        var responses = await Task.WhenAll(requests);

        Assert.AreEqual(2L, blocked);
        Assert.AreEqual(1, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
        await AssertConflictAsync(responses.Single(x => x.StatusCode != HttpStatusCode.Created), "pass-exhausted");
        Assert.AreEqual(0, await RemainingVisitsAsync(app, user));
        await using var context = app.CreateContext();
        Assert.AreEqual(1, await context.Enrollments.CountAsync(x => x.UserId == user.Id));

        DisposeAll(responses, []);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Enroll_TwentyUsersOnFiveSeatsWithoutHeldLock_ExactlyFiveCreated()
    {
        await using var app = await TestApp.StartAsync();
        var classId = await app.Data.ClassAsync(capacity: 5);
        var clients = new List<HttpClient>();
        for (var i = 0; i < 20; i++)
        {
            var user = await app.Data.UserAsync(remainingVisits: 10);
            clients.Add(await app.CreateClientAsync(user.Email));
        }

        var responses = await Task.WhenAll(clients.Select(x => EnrollAsync(x, classId, 1)));

        Assert.AreEqual(5, responses.Count(x => x.StatusCode == HttpStatusCode.Created));
        foreach (var response in responses.Where(x => x.StatusCode != HttpStatusCode.Created))
        {
            await AssertConflictAsync(response, "capacity-exceeded");
        }

        await using var context = app.CreateContext();
        Assert.AreEqual(5, await context.Enrollments.Where(x => x.ClassId == classId && x.Status == "active").SumAsync(x => x.Seats));

        DisposeAll(responses, clients);
    }

    [TestMethod]
    public async Task Enroll_ClockReachesStartWhileWaitingForClass_Returns409ClassStartedWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = app.Clock.GetUtcNow().UtcDateTime.AddHours(1);
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldClassAsync(app, classId);
        var request = EnrollAsync(client, classId, 1);
        var blocked = await held.WaitForBlockedCountAsync(1);
        app.Clock.SetUtcNow(startsAt);
        await held.DisposeAsync();
        using var response = await request;

        Assert.AreEqual(1L, blocked);
        await AssertConflictAsync(response, "class-started");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Enroll_ClockMovesBeforeStartWhileWaitingForClass_Returns201WithMovedCreatedAt()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = app.Clock.GetUtcNow().UtcDateTime.AddHours(1);
        var moved = startsAt.AddMinutes(-30);
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldClassAsync(app, classId);
        var request = EnrollAsync(client, classId, 1);
        var blocked = await held.WaitForBlockedCountAsync(1);
        app.Clock.SetUtcNow(moved);
        await held.DisposeAsync();
        using var response = await request;

        Assert.AreEqual(1L, blocked);
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, text);
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual(moved.ToString("yyyy-MM-ddTHH:mm:ssZ"), body.RootElement.GetProperty("createdAt").GetString());
        await using var context = app.CreateContext();
        Assert.AreEqual(moved, await context.Enrollments.Where(x => x.UserId == user.Id).Select(x => x.CreatedAt).SingleAsync());
    }

    [TestMethod]
    public async Task Enroll_DayEndsAfterValidUntilWhileWaitingForClass_Returns409PassExpiredWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var user = await app.Data.UserAsync(remainingVisits: 10, validUntil: today);
        var classId = await app.Data.ClassAsync(startsAt: now.AddDays(10));
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldClassAsync(app, classId);
        var request = EnrollAsync(client, classId, 1);
        var blocked = await held.WaitForBlockedCountAsync(1);
        app.Clock.SetUtcNow(today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        await held.DisposeAsync();
        using var response = await request;

        Assert.AreEqual(1L, blocked);
        await AssertConflictAsync(response, "pass-expired");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Enroll_ClockReachesStartWhileWaitingForPass_Returns409ClassStartedWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = app.Clock.GetUtcNow().UtcDateTime.AddHours(1);
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldPassAsync(app, user);
        var request = EnrollAsync(client, classId, 1);
        var blocked = await held.WaitForBlockedCountAsync(1);
        app.Clock.SetUtcNow(startsAt);
        await held.DisposeAsync();
        using var response = await request;

        Assert.AreEqual(1L, blocked);
        await AssertConflictAsync(response, "class-started");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Enroll_ClassLockTimeout_Returns503WithoutTraceAndCountsRejection()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        using var collector = new MetricCollector<long>(
            app.Services.GetRequiredService<IMeterFactory>(),
            "FitnessClub.Api",
            "club.requests.rejected");
        await using var held = await HoldClassAsync(app, classId);

        using var response = await EnrollAsync(client, classId, 1);

        await AssertLockTimeoutAsync(response);
        await AssertNoTraceAsync(app, user, 10);
        var measurement = collector.GetMeasurementSnapshot().Single();
        Assert.AreEqual(1, measurement.Value);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, object?> { ["operation"] = "enroll", ["reason"] = "lock-timeout" },
            measurement.Tags.ToDictionary(x => x.Key, x => x.Value));
    }

    [TestMethod]
    public async Task Enroll_PassLockTimeout_Returns503AndReleasesClass()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        using var otherClient = await app.CreateClientAsync(other.Email);
        await using var held = await HoldPassAsync(app, user);

        using var response = await EnrollAsync(client, classId, 1);
        using var next = await EnrollAsync(otherClient, classId, 1);

        await AssertLockTimeoutAsync(response);
        await AssertNoTraceAsync(app, user, 10);
        Assert.AreEqual(HttpStatusCode.Created, next.StatusCode, await next.Content.ReadAsStringAsync());
        Assert.AreEqual(9, await RemainingVisitsAsync(app, other));
    }

    private static Task<HeldLock> HoldClassAsync(TestApp app, long classId)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");
    }

    private static Task<HeldLock> HoldPassAsync(TestApp app, TestUser user)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM passes WHERE user_id = {user.Id} FOR UPDATE");
    }

    private static async Task AssertLockTimeoutAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, text);
        Assert.AreEqual("1", response.Headers.GetValues("Retry-After").Single());
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        CollectionAssert.AreEqual(
            new[] { "reason", "status", "title", "traceId", "type" },
            root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.AreEqual(503, root.GetProperty("status").GetInt32());
        Assert.AreEqual("Service Unavailable", root.GetProperty("title").GetString());
        Assert.AreEqual("https://tools.ietf.org/html/rfc9110#section-15.6.4", root.GetProperty("type").GetString());
        Assert.AreEqual("lock-timeout", root.GetProperty("reason").GetString());
        StringAssert.Matches(root.GetProperty("traceId").GetString(), new Regex(TraceParent));
    }

    private static void DisposeAll(IEnumerable<HttpResponseMessage> responses, IEnumerable<HttpClient> clients)
    {
        foreach (var response in responses)
        {
            response.Dispose();
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }
    }
}
