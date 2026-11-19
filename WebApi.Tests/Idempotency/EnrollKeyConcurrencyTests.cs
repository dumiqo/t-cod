using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Idempotency;

[TestClass]
public class EnrollKeyConcurrencyTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task Enroll_TwoConcurrentSameKeyAndBody_BothReturn201WithSameBodyAndOneEnrollment()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":2}}";

        await using var held = await HoldClassAsync(app, classId);
        var requests = new[] { PostAsync(client, json, "key-1"), PostAsync(client, json, "key-1") };
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        var responses = await Task.WhenAll(requests);

        Assert.AreEqual(2L, blocked);
        var texts = new List<string>();
        foreach (var response in responses)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, text);
            texts.Add(text);
        }

        Assert.AreEqual(texts[0], texts[1]);
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        using var body = JsonDocument.Parse(texts[0]);
        Assert.AreEqual(enrollment.Id, body.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(2, body.RootElement.GetProperty("seats").GetInt32());
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(
            $"{classId}|2|{enrollment.Id}",
            await context.IdempotencyKeys.Where(x => x.UserId == user.Id)
                .Select(x => $"{x.ClassId}|{x.Seats}|{x.ResultId}")
                .SingleAsync());
        Assert.AreEqual(1, CreatedEvents(app).Count);
        Assert.AreEqual(2, MetricSum(app, "club.visits.charged"));
        var replays = ReplayEvents(app);
        Assert.AreEqual(1, replays.Count);
        Assert.AreEqual(enrollment.Id.ToString(), replays[0].GetStructuredStateValue("ResultId"));
        Assert.AreEqual(1, MetricSum(app, "club.idempotency.replayed"));
        Assert.AreEqual(0, RejectedEvents(app).Count);

        DisposeAll(responses);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Enroll_SameKeyOtherClassWaitsOnKeyAndFirstCommits_Returns409IdempotencyKeyReused()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var first = await app.Data.ClassAsync();
        var second = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldPassAsync(app, user);
        var requestA = PostAsync(client, $"{{\"classId\":{first},\"seats\":1}}", "key-1");
        var blockedA = await held.WaitForBlockedCountAsync(1);
        var requestB = PostAsync(client, $"{{\"classId\":{second},\"seats\":1}}", "key-1");
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        using var responseA = await requestA;
        using var responseB = await requestB;

        Assert.AreEqual(1L, blockedA);
        Assert.AreEqual(2L, blocked);
        Assert.AreEqual(HttpStatusCode.Created, responseA.StatusCode, await responseA.Content.ReadAsStringAsync());
        await AssertConflictAsync(responseB, "idempotency-key-reused");
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(first, enrollment.ClassId);
        Assert.AreEqual(9, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(
            $"{first}|1|{enrollment.Id}",
            await context.IdempotencyKeys.Where(x => x.UserId == user.Id)
                .Select(x => $"{x.ClassId}|{x.Seats}|{x.ResultId}")
                .SingleAsync());
        await AssertNoEnrollmentsAsync(app, second);
        Assert.AreEqual(1, CreatedEvents(app).Count);
        Assert.AreEqual(0, ReplayEvents(app).Count);
        Assert.AreEqual(0, MetricSum(app, "club.idempotency.replayed"));
        AssertRejectionsCounted(app, "idempotency-key-reused", 1);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Enroll_SameKeyOtherClassWaitsOnKeyAndFirstRollsBack_SecondReturns201AndOwnsKey()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync(remainingVisits: 10);
        var first = await app.Data.ClassAsync(capacity: 1);
        var second = await app.Data.ClassAsync();
        await app.Data.EnrollmentAsync(other.Id, first, 1);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldPassAsync(app, user);
        var requestA = PostAsync(client, $"{{\"classId\":{first},\"seats\":1}}", "key-1");
        var blockedA = await held.WaitForBlockedCountAsync(1);
        var requestB = PostAsync(client, $"{{\"classId\":{second},\"seats\":1}}", "key-1");
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        using var responseA = await requestA;
        using var responseB = await requestB;

        Assert.AreEqual(1L, blockedA);
        Assert.AreEqual(2L, blocked);
        await AssertConflictAsync(responseA, "capacity-exceeded");
        var textB = await responseB.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, responseB.StatusCode, textB);
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(second, enrollment.ClassId);
        using var body = JsonDocument.Parse(textB);
        Assert.AreEqual(enrollment.Id, body.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(9, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(
            $"{second}|1|{enrollment.Id}",
            await context.IdempotencyKeys.Where(x => x.UserId == user.Id)
                .Select(x => $"{x.ClassId}|{x.Seats}|{x.ResultId}")
                .SingleAsync());
        Assert.AreEqual(1, CreatedEvents(app).Count);
        Assert.AreEqual(0, ReplayEvents(app).Count);
        Assert.AreEqual(0, MetricSum(app, "club.idempotency.replayed"));
        AssertRejectionsCounted(app, "capacity-exceeded", 1);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Enroll_KeyWaitExceedsLockTimeout_Returns503WithoutTraceAndRepeatRunsAnew()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var first = await app.Data.ClassAsync();
        var second = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{second},\"seats\":1}}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("key-1")));

        await using var held = await HeldLock.AcquireAsync(
            app.ConnectionString,
            $"""
            INSERT INTO idempotency_keys (user_id, operation, key_hash, class_id, seats)
            VALUES ({user.Id}, 'enroll', decode('{hash}', 'hex'), {first}, 1)
            """);
        using var response = await PostAsync(client, json, "key-1");

        await AssertServiceUnavailableAsync(response);
        await held.DisposeAsync();
        await AssertNoTraceAsync(app, user, 10);
        using var repeat = await PostAsync(client, json, "key-1");

        var text = await repeat.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, text);
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(second, enrollment.ClassId);
        Assert.AreEqual(9, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(
            $"{second}|1|{enrollment.Id}",
            await context.IdempotencyKeys.Where(x => x.UserId == user.Id)
                .Select(x => $"{x.ClassId}|{x.Seats}|{x.ResultId}")
                .SingleAsync());
    }

    [TestMethod]
    public async Task Enroll_RepeatAfterClassLock503_RunsAnewAndReturns201()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";

        await using (await HoldClassAsync(app, classId))
        {
            using var response = await PostAsync(client, json, "key-1");
            await AssertServiceUnavailableAsync(response);
        }

        using var repeat = await PostAsync(client, json, "key-1");

        var text = await repeat.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, text);
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual(enrollment.Id, body.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(9, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(1, CreatedEvents(app).Count);
    }

    private static Task<HeldLock> HoldClassAsync(TestApp app, long classId)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");
    }

    private static Task<HeldLock> HoldPassAsync(TestApp app, TestUser user)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM passes WHERE user_id = {user.Id} FOR UPDATE");
    }

    private static async Task AssertServiceUnavailableAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, text);
        Assert.AreEqual("1", response.Headers.GetValues("Retry-After").Single());
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual("lock-timeout", body.RootElement.GetProperty("reason").GetString());
    }

    private static void DisposeAll(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
