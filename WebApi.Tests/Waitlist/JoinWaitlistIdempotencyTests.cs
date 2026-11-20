using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Enrollments;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class JoinWaitlistIdempotencyTests
{
    [TestMethod]
    public async Task Join_RepeatSameKeyAndBody_Returns201WithFirstBodyAndOneEntry()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":2}}";
        var first = await CreatedTextAsync(await PostAsync(client, json, "key-1"));
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        var repeat = await CreatedTextAsync(await PostAsync(client, json, "key-1"));

        Assert.AreEqual(first, repeat);
        var entry = await SingleEntryAsync(app, user);
        using var body = JsonDocument.Parse(repeat);
        Assert.AreEqual(entry.Id, body.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(1, body.RootElement.GetProperty("position").GetInt32());
        Assert.AreEqual("waiting", entry.Status);
        CollectionAssert.AreEqual(new[] { $"join-waitlist|{classId}|2|{entry.Id}|1" }, await KeysAsync(app, user));
    }

    [TestMethod]
    [DataRow("class-cancelled")]
    [DataRow("class-started")]
    [DataRow("seats-available")]
    [DataRow("already-waiting")]
    public async Task Join_RepeatAfterReasonAppeared_Returns201WithFirstBody(string reason)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var probe = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        using var probeClient = await app.CreateClientAsync(probe.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";
        var first = await CreatedTextAsync(await PostAsync(client, json, "key-1"));
        await using (var context = app.CreateContext())
        {
            switch (reason)
            {
                case "class-cancelled":
                    await context.Classes.IgnoreQueryFilters().Where(x => x.Id == classId)
                        .ExecuteUpdateAsync(x => x.SetProperty(c => c.IsCancelled, true));
                    break;
                case "class-started":
                    app.Clock.Advance(TimeSpan.FromDays(2));
                    break;
                case "seats-available":
                    await context.Enrollments.Where(x => x.ClassId == classId)
                        .ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, "cancelled").SetProperty(e => e.RefundedVisits, 0));
                    break;
            }
        }

        using var control = await PostAsync(reason == "already-waiting" ? client : probeClient, json);
        await EnrollApi.AssertConflictAsync(control, reason);
        var before = await WaitlistSnapshotAsync(app, classId);

        var repeat = await CreatedTextAsync(await PostAsync(client, json, "key-1"));

        Assert.AreEqual(first, repeat);
        CollectionAssert.AreEqual(before, await WaitlistSnapshotAsync(app, classId));
        var entry = await SingleEntryAsync(app, user);
        CollectionAssert.AreEqual(new[] { $"join-waitlist|{classId}|1|{entry.Id}|1" }, await KeysAsync(app, user));
    }

    [TestMethod]
    public async Task Join_RepeatAfterEntryAheadLeft_ReturnsFirstPosition()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        using var otherClient = await app.CreateClientAsync(other.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";
        var ahead = await CreatedTextAsync(await PostAsync(otherClient, json));
        var first = await CreatedTextAsync(await PostAsync(client, json, "key-1"));
        using var aheadBody = JsonDocument.Parse(ahead);
        using var left = await LeaveAsync(otherClient, aheadBody.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(HttpStatusCode.OK, left.StatusCode, await left.Content.ReadAsStringAsync());

        var repeat = await CreatedTextAsync(await PostAsync(client, json, "key-1"));

        Assert.AreEqual(first, repeat);
        using var body = JsonDocument.Parse(repeat);
        Assert.AreEqual(2, body.RootElement.GetProperty("position").GetInt32());
    }

    [TestMethod]
    public async Task Join_RepeatAfterPromotion_Returns201WithFirstBodyAndEntryUnchanged()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";
        var first = await CreatedTextAsync(await PostAsync(client, json, "key-1"));
        await using (var context = app.CreateContext())
        {
            await context.WaitlistEntries.Where(x => x.UserId == user.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(e => e.Status, "promoted"));
        }

        var before = await WaitlistSnapshotAsync(app, classId);

        var repeat = await CreatedTextAsync(await PostAsync(client, json, "key-1"));

        Assert.AreEqual(first, repeat);
        CollectionAssert.AreEqual(before, await WaitlistSnapshotAsync(app, classId));
        Assert.AreEqual("promoted", (await SingleEntryAsync(app, user)).Status);
    }

    [TestMethod]
    public async Task Join_RepeatAfterLeaving_Returns201WithFirstBodyAndDoesNotRequeue()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";
        var first = await CreatedTextAsync(await PostAsync(client, json, "key-1"));
        var entry = await SingleEntryAsync(app, user);
        using var left = await LeaveAsync(client, entry.Id);
        Assert.AreEqual(HttpStatusCode.OK, left.StatusCode, await left.Content.ReadAsStringAsync());
        var before = await WaitlistSnapshotAsync(app, classId);

        var repeat = await CreatedTextAsync(await PostAsync(client, json, "key-1"));

        Assert.AreEqual(first, repeat);
        CollectionAssert.AreEqual(before, await WaitlistSnapshotAsync(app, classId));
        Assert.AreEqual("cancelled", (await SingleEntryAsync(app, user)).Status);

        var rejoin = await CreatedTextAsync(await PostAsync(client, json, "key-2"));

        using var body = JsonDocument.Parse(rejoin);
        var newId = body.RootElement.GetProperty("id").GetInt64();
        Assert.AreNotEqual(entry.Id, newId);
        await using var context = app.CreateContext();
        CollectionAssert.AreEqual(
            new[] { $"{entry.Id}|cancelled", $"{newId}|waiting" },
            await context.WaitlistEntries.Where(x => x.UserId == user.Id).OrderBy(x => x.Id)
                .Select(x => x.Id + "|" + x.Status).ToArrayAsync());
    }

    [TestMethod]
    public async Task Join_KeyUsedByEnroll_IsNotReplayAndCreatesEntry()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var freeClassId = await app.Data.ClassAsync();
        var fullClassId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        using var enrolled = await EnrollApi.PostAsync(client, $"{{\"classId\":{freeClassId},\"seats\":1}}", "key-1");
        Assert.AreEqual(HttpStatusCode.Created, enrolled.StatusCode, await enrolled.Content.ReadAsStringAsync());

        var joined = await CreatedTextAsync(await PostAsync(client, $"{{\"classId\":{fullClassId},\"seats\":1}}", "key-1"));

        var entry = await SingleEntryAsync(app, user);
        using var body = JsonDocument.Parse(joined);
        Assert.AreEqual(entry.Id, body.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(fullClassId, entry.ClassId);
        await using var context = app.CreateContext();
        var enrollmentId = await context.Enrollments.Where(x => x.UserId == user.Id).Select(x => x.Id).SingleAsync();
        CollectionAssert.AreEquivalent(
            new[] { $"enroll|{freeClassId}|1|{enrollmentId}|", $"join-waitlist|{fullClassId}|1|{entry.Id}|1" },
            await KeysAsync(app, user));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Join_SameKeyOtherBody_Returns409IdempotencyKeyReusedWithoutTrace(bool otherClass)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        var otherClassId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        var first = await CreatedTextAsync(await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", "key-1"));
        var keysBefore = await KeysAsync(app, user);
        var before = await WaitlistSnapshotAsync(app, classId);
        var otherBefore = await WaitlistSnapshotAsync(app, otherClassId);
        var json = otherClass ? $"{{\"classId\":{otherClassId},\"seats\":1}}" : $"{{\"classId\":{classId},\"seats\":2}}";

        using var response = await PostAsync(client, json, "key-1");

        await EnrollApi.AssertConflictAsync(response, "idempotency-key-reused");
        CollectionAssert.AreEqual(before, await WaitlistSnapshotAsync(app, classId));
        CollectionAssert.AreEqual(otherBefore, await WaitlistSnapshotAsync(app, otherClassId));
        CollectionAssert.AreEqual(keysBefore, await KeysAsync(app, user));
        var repeat = await CreatedTextAsync(await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", "key-1"));
        Assert.AreEqual(first, repeat);
    }

    [TestMethod]
    [DataRow("class-cancelled")]
    [DataRow("class-started")]
    [DataRow("already-waiting")]
    [DataRow("seats-available")]
    public async Task Join_SameKeyOtherBodyAndOtherReason_Returns409IdempotencyKeyReused(string reason)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var otherClassId = reason switch
        {
            "class-cancelled" => await app.Data.ClassAsync(capacity: 10, cancelled: true),
            "class-started" => await app.Data.ClassAsync(capacity: 10, startsAt: now.AddHours(-1)),
            "already-waiting" => await FullClassAsync(app),
            _ => await app.Data.ClassAsync(capacity: 10)
        };
        if (reason == "already-waiting")
        {
            await EntryAsync(app, user.Id, otherClassId, 1);
        }

        using var client = await app.CreateClientAsync(user.Email);
        await CreatedTextAsync(await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", "key-1"));
        var json = $"{{\"classId\":{otherClassId},\"seats\":1}}";
        using var control = await PostAsync(client, json);
        await EnrollApi.AssertConflictAsync(control, reason);
        var keysBefore = await KeysAsync(app, user);
        var otherBefore = await WaitlistSnapshotAsync(app, otherClassId);

        using var response = await PostAsync(client, json, "key-1");

        await EnrollApi.AssertConflictAsync(response, "idempotency-key-reused");
        CollectionAssert.AreEqual(otherBefore, await WaitlistSnapshotAsync(app, otherClassId));
        CollectionAssert.AreEqual(keysBefore, await KeysAsync(app, user));
    }

    [TestMethod]
    public async Task Join_SameKeyUnknownClass_Returns404()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        await CreatedTextAsync(await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", "key-1"));
        var keysBefore = await KeysAsync(app, user);

        using var response = await PostAsync(client, "{\"classId\":900000000000,\"seats\":1}", "key-1");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(keysBefore, await KeysAsync(app, user));
    }

    [TestMethod]
    public async Task Join_SameKeyAfterSeatsAvailableAndClassFilled_Returns201AsNewEntry()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var filler = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(capacity: 2);
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";
        using var rejected = await PostAsync(client, json, "key-1");
        await EnrollApi.AssertConflictAsync(rejected, "seats-available");
        await AssertNoTraceAsync(app, user, 10);
        await app.Data.EnrollmentAsync(filler.Id, classId, 2);

        var created = await CreatedTextAsync(await PostAsync(client, json, "key-1"));

        var entry = await SingleEntryAsync(app, user);
        using var body = JsonDocument.Parse(created);
        Assert.AreEqual(entry.Id, body.RootElement.GetProperty("id").GetInt64());
        Assert.AreEqual(1, body.RootElement.GetProperty("position").GetInt32());
        CollectionAssert.AreEqual(new[] { $"join-waitlist|{classId}|1|{entry.Id}|1" }, await KeysAsync(app, user));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Join_TwoConcurrentSameKeyAndBody_BothReturn201WithSameBodyAndOneEntry()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";

        await using var held = await HeldLock.AcquireAsync(
            app.ConnectionString,
            $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");
        var requests = new[] { PostAsync(client, json, "key-1"), PostAsync(client, json, "key-1") };
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        var responses = await Task.WhenAll(requests);

        Assert.AreEqual(2L, blocked);
        var first = await CreatedTextAsync(responses[0]);
        var second = await CreatedTextAsync(responses[1]);
        Assert.AreEqual(first, second);
        var entry = await SingleEntryAsync(app, user);
        using var body = JsonDocument.Parse(first);
        Assert.AreEqual(entry.Id, body.RootElement.GetProperty("id").GetInt64());
        CollectionAssert.AreEqual(new[] { $"join-waitlist|{classId}|1|{entry.Id}|1" }, await KeysAsync(app, user));
        Assert.AreEqual(1, JoinedEvents(app).Count);
        Assert.AreEqual(1L, EnrollApi.MetricSum(app, "club.waitlist.joined"));
        Assert.AreEqual(1, EnrollApi.ReplayEvents(app).Count);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Join_SameKeyOtherClassWaitsOnKeyAndFirstCommits_Returns409IdempotencyKeyReused()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var first = await FullClassAsync(app);
        var second = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HeldLock.AcquireAsync(
            app.ConnectionString,
            $"""
            INSERT INTO waitlist_entries (user_id, class_id, seats, status, created_at)
            VALUES ({user.Id}, {first}, 1, 'waiting', now())
            """);
        var requestA = PostAsync(client, $"{{\"classId\":{first},\"seats\":1}}", "key-1");
        var blockedA = await held.WaitForBlockedCountAsync(1);
        var requestB = PostAsync(client, $"{{\"classId\":{second},\"seats\":1}}", "key-1");
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        using var responseA = await requestA;
        using var responseB = await requestB;

        Assert.AreEqual(1L, blockedA);
        Assert.AreEqual(2L, blocked);
        await CreatedTextAsync(responseA);
        await EnrollApi.AssertConflictAsync(responseB, "idempotency-key-reused");
        var entry = await SingleEntryAsync(app, user);
        Assert.AreEqual(first, entry.ClassId);
        CollectionAssert.AreEqual(new[] { $"join-waitlist|{first}|1|{entry.Id}|1" }, await KeysAsync(app, user));
        CollectionAssert.AreEqual(Array.Empty<string>(), await WaitlistSnapshotAsync(app, second));
    }

    private static async Task<string> CreatedTextAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, text);

            return text;
        }
    }

    private static Task<HttpResponseMessage> LeaveAsync(HttpClient client, long id)
    {
        return client.PostAsync($"/enrollments/waitlist/{id}/cancel", null);
    }

    private static async Task<WaitlistEntry> SingleEntryAsync(TestApp app, TestUser user)
    {
        await using var context = app.CreateContext();

        return await context.WaitlistEntries.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
    }

    private static async Task<string[]> KeysAsync(TestApp app, TestUser user)
    {
        await using var context = app.CreateContext();
        var keys = await context.IdempotencyKeys.AsNoTracking().Where(x => x.UserId == user.Id).OrderBy(x => x.Id).ToListAsync();

        return keys
            .Select(x => $"{IdempotentOperations.ToValue(x.Operation)}|{x.ClassId}|{x.Seats}|{x.ResultId}|{x.ResultPosition}")
            .ToArray();
    }
}
