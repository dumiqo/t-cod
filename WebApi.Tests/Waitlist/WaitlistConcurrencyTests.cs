using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Enrollments;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class WaitlistConcurrencyTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task Join_TwoClientsWaitForClassLock_PositionsAndIdsFollowCommitOrder()
    {
        await using var app = await TestApp.StartAsync();
        var a = await app.Data.UserAsync();
        var b = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        using var clientA = await app.CreateClientAsync(a.Email);
        using var clientB = await app.CreateClientAsync(b.Email);

        await using var held = await HoldClassAsync(app, classId);
        var requestA = JoinAsync(clientA, classId, 1);
        var blockedA = await held.WaitForBlockedCountAsync(1);
        var requestB = JoinAsync(clientB, classId, 1);
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        var (idA, positionA) = await CreatedAsync(await requestA);
        var (idB, positionB) = await CreatedAsync(await requestB);

        Assert.AreEqual(1L, blockedA);
        Assert.AreEqual(2L, blocked);
        Assert.AreEqual(1, positionA);
        Assert.AreEqual(2, positionB);
        await using var context = app.CreateContext();
        var order = await context.WaitlistEntries.Where(x => x.ClassId == classId).OrderBy(x => x.Id).Select(x => x.UserId).ToListAsync();
        CollectionAssert.AreEqual(new[] { a.Id, b.Id }, order);
        Assert.IsTrue(idA < idB, $"{idA} < {idB}");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Join_SeatsFreedWhileWaitingForClassLock_Returns409SeatsAvailableWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var filler = await app.Data.UserAsync(remainingVisits: 2);
        var classId = await app.Data.ClassAsync(capacity: 2);
        var enrollmentId = await app.Data.EnrollmentAsync(filler.Id, classId, 2);
        var user = await app.Data.UserAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldClassAsync(app, classId);
        var request = JoinAsync(client, classId, 1);
        await held.WaitForBlockedAsync(1);
        await held.ExecuteAsync(
            $"UPDATE enrollments SET status = 'cancelled', cancelled_at = '{now:O}', refunded_visits = 2 WHERE id = {enrollmentId}");
        await held.CommitAsync();
        using var response = await request;

        await EnrollApi.AssertConflictAsync(response, "seats-available");
        await AssertNoTraceAsync(app, user, 10);
        CollectionAssert.AreEqual(Array.Empty<string>(), await WaitlistSnapshotAsync(app, classId));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Join_SameClientTwiceConcurrently_OneEntryAndSecondAlreadyWaiting()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldClassAsync(app, classId);
        var requests = new[] { JoinAsync(client, classId, 1), JoinAsync(client, classId, 1) };
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        var responses = await Task.WhenAll(requests);

        Assert.AreEqual(2L, blocked);
        var created = responses.Single(x => x.StatusCode == HttpStatusCode.Created);
        var (id, position) = await CreatedAsync(created);
        await EnrollApi.AssertConflictAsync(responses.Single(x => x.StatusCode != HttpStatusCode.Created), "already-waiting");
        Assert.AreEqual(1, position);
        Assert.AreEqual(1, JoinedEvents(app).Count);
        Assert.AreEqual(1L, EnrollApi.MetricSum(app, "club.waitlist.joined"));
        Assert.AreEqual(1, EnrollApi.RejectedEvents(app).Count(x => x.GetStructuredStateValue("Reason") == "already-waiting"));
        await using var context = app.CreateContext();
        Assert.AreEqual(id, await context.WaitlistEntries.Where(x => x.UserId == user.Id).Select(x => x.Id).SingleAsync());
        Assert.AreEqual(id, await context.IdempotencyKeys.Where(x => x.UserId == user.Id).Select(x => x.ResultId).SingleAsync());
    }

    [TestMethod]
    public async Task Join_ClockReachesStartWhileWaitingForClassLock_Returns409ClassStartedWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = app.Clock.GetUtcNow().UtcDateTime.AddHours(1);
        var filler = await app.Data.UserAsync(remainingVisits: 1);
        var classId = await app.Data.ClassAsync(capacity: 1, startsAt: startsAt);
        await app.Data.EnrollmentAsync(filler.Id, classId, 1);
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldClassAsync(app, classId);
        var request = JoinAsync(client, classId, 1);
        var blocked = await held.WaitForBlockedCountAsync(1);
        app.Clock.SetUtcNow(startsAt);
        await held.DisposeAsync();
        using var response = await request;

        Assert.AreEqual(1L, blocked);
        await EnrollApi.AssertConflictAsync(response, "class-started");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Join_ClassLockTimeout_Returns503WithoutKeyAndSameKeyLaterCreates()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HoldClassAsync(app, classId);

        using (var response = await PostAsync(client, json, "key-1"))
        {
            await AssertLockTimeoutAsync(response);
        }

        await AssertNoTraceAsync(app, user, 10);
        await held.DisposeAsync();
        var (id, position) = await CreatedAsync(await PostAsync(client, json, "key-1"));

        Assert.AreEqual(1, position);
        await using var context = app.CreateContext();
        Assert.AreEqual(id, await context.WaitlistEntries.Where(x => x.UserId == user.Id).Select(x => x.Id).SingleAsync());
        Assert.AreEqual(id, await context.IdempotencyKeys.Where(x => x.UserId == user.Id).Select(x => x.ResultId).SingleAsync());
    }

    [TestMethod]
    public async Task Leave_ClassLockTimeout_Returns503AndEntryUnchanged()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, user.Id, classId, 1);
        var snapshot = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HoldClassAsync(app, classId);

        using var response = await LeaveAsync(client, id);

        await AssertLockTimeoutAsync(response);
        CollectionAssert.AreEqual(snapshot, await WaitlistSnapshotAsync(app, classId));
    }

    [TestMethod]
    [DataRow("cancelled", 200, null)]
    [DataRow("promoted", 409, "already-promoted")]
    public async Task Leave_SettledEntryWhileClassLocked_AnswersWithoutWaitingForLock(string status, int code, string? reason)
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, user.Id, classId, 1, status);
        var snapshot = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HoldClassAsync(app, classId);

        using var response = await LeaveAsync(client, id);

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(code, (int)response.StatusCode, text);
        if (reason is null)
        {
            using var body = JsonDocument.Parse(text);
            Assert.AreEqual(id, body.RootElement.GetProperty("id").GetInt64());
            Assert.AreEqual("cancelled", body.RootElement.GetProperty("status").GetString());
        }
        else
        {
            await EnrollApi.AssertConflictAsync(response, reason);
        }

        CollectionAssert.AreEqual(snapshot, await WaitlistSnapshotAsync(app, classId));
    }

    private static async Task<(long Id, int Position)> CreatedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, text);
            using var body = JsonDocument.Parse(text);

            return (body.RootElement.GetProperty("id").GetInt64(), body.RootElement.GetProperty("position").GetInt32());
        }
    }

    private static async Task AssertLockTimeoutAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, text);
        Assert.AreEqual("1", response.Headers.GetValues("Retry-After").Single());
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual("lock-timeout", body.RootElement.GetProperty("reason").GetString());
    }

    private static Task<HttpResponseMessage> LeaveAsync(HttpClient client, long id)
    {
        return client.PostAsync($"/enrollments/waitlist/{id}/cancel", null);
    }

    private static Task<HeldLock> HoldClassAsync(TestApp app, long classId)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");
    }
}
