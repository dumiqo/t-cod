using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.EnrollApi;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class PromotionConcurrencyTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task Cancel_CrossCancelsEachWaitingOnOthersClass_BothPromotedWithout503()
    {
        await using var app = await TestApp.StartAsync();
        var a = await app.Data.UserAsync(remainingVisits: 5);
        var b = await app.Data.UserAsync(remainingVisits: 5);
        var classX = await app.Data.ClassAsync(capacity: 1);
        var classY = await app.Data.ClassAsync(capacity: 1);
        var enrollmentA = await app.Data.EnrollmentAsync(a.Id, classX, 1);
        var enrollmentB = await app.Data.EnrollmentAsync(b.Id, classY, 1);
        var entryB = await EntryAsync(app, b.Id, classX, 1);
        var entryA = await EntryAsync(app, a.Id, classY, 1);
        using var clientA = await app.CreateClientAsync(a.Email);
        using var clientB = await app.CreateClientAsync(b.Email);

        await using var held = await HeldLock.AcquireAsync(
            app.ConnectionString, $"SELECT 1 FROM passes WHERE user_id IN ({a.Id}, {b.Id}) FOR UPDATE");
        var requests = new[] { CancelAsync(clientA, enrollmentA), CancelAsync(clientB, enrollmentB) };
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        var responses = await Task.WhenAll(requests);

        Assert.AreEqual(2L, blocked);
        await AssertCancelledAsync(responses[0], enrollmentA, 1);
        await AssertCancelledAsync(responses[1], enrollmentB, 1);
        CollectionAssert.AreEqual(new[] { "promoted|1", "promoted|1" }, await PromotionOnCancelTests.QueueAsync(app, entryB, entryA));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, a));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, b));
        DisposeAll(responses);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(true, 409, "promoted|1", "waiting|-")]
    [DataRow(false, 200, "cancelled|-", "promoted|1")]
    public async Task Cancel_RacesWithLeave_LeaverGetsNoSeatAndOrderKept(bool cancelFirst, int leaveStatus, string first, string second)
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var users = new[] { await app.Data.UserAsync(remainingVisits: 5), await app.Data.UserAsync(remainingVisits: 5) };
        var classId = await app.Data.ClassAsync(capacity: 1);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 1);
        var firstId = await EntryAsync(app, users[0].Id, classId, 1);
        var secondId = await EntryAsync(app, users[1].Id, classId, 1);
        using var ownerClient = await app.CreateClientAsync(owner.Email);
        using var leaverClient = await app.CreateClientAsync(users[0].Email);

        await using var held = await HoldClassAsync(app, classId);
        var (cancel, leave) = await InOrderAsync(
            held, cancelFirst, () => CancelAsync(ownerClient, ownerEnrollment), () => LeaveAsync(leaverClient, firstId));

        await AssertCancelledAsync(cancel, ownerEnrollment, 1);
        var leaveText = await leave.Content.ReadAsStringAsync();
        Assert.AreEqual(leaveStatus, (int)leave.StatusCode, leaveText);
        if (leaveStatus == 409)
        {
            await AssertConflictAsync(leave, "already-promoted");
        }
        else
        {
            using var body = JsonDocument.Parse(leaveText);
            Assert.AreEqual(firstId, body.RootElement.GetProperty("id").GetInt64());
            Assert.AreEqual("cancelled", body.RootElement.GetProperty("status").GetString());
        }

        CollectionAssert.AreEqual(new[] { first, second }, await PromotionOnCancelTests.QueueAsync(app, firstId, secondId));
        Assert.AreEqual(first == "promoted|1" ? 4 : 5, await RemainingVisitsAsync(app, users[0]));
        Assert.AreEqual(second == "promoted|1" ? 4 : 5, await RemainingVisitsAsync(app, users[1]));
        Assert.AreEqual(1L, await ActiveSeatsAsync(app, classId));
        cancel.Dispose();
        leave.Dispose();
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(true, 1, 201, 1, "promoted|1")]
    [DataRow(true, 2, 409, 0, "promoted|1")]
    [DataRow(false, 1, 201, 2, "promoted|1")]
    [DataRow(false, 2, 201, 2, "promoted|1")]
    public async Task Cancel_RacesWithJoin_OldWaitersPromotedBeforeNewcomer(
        bool cancelFirst, int ownerSeats, int joinStatus, int position, string old)
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var newcomer = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: ownerSeats);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, ownerSeats);
        var oldId = await EntryAsync(app, waiter.Id, classId, 1);
        using var ownerClient = await app.CreateClientAsync(owner.Email);
        using var newcomerClient = await app.CreateClientAsync(newcomer.Email);

        await using var held = await HoldClassAsync(app, classId);
        var (cancel, join) = await InOrderAsync(
            held, cancelFirst, () => CancelAsync(ownerClient, ownerEnrollment), () => JoinAsync(newcomerClient, classId, 1));

        await AssertCancelledAsync(cancel, ownerEnrollment, ownerSeats);
        var joinText = await join.Content.ReadAsStringAsync();
        Assert.AreEqual(joinStatus, (int)join.StatusCode, joinText);
        CollectionAssert.AreEqual(new[] { old }, await PromotionOnCancelTests.QueueAsync(app, oldId));
        Assert.AreEqual(4, await RemainingVisitsAsync(app, waiter));
        await using var context = app.CreateContext();
        var newEntry = await context.WaitlistEntries.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == newcomer.Id);
        if (joinStatus == 409)
        {
            await AssertConflictAsync(join, "seats-available");
            Assert.IsNull(newEntry);
            Assert.AreEqual(1L, await ActiveSeatsAsync(app, classId));
        }
        else
        {
            using var body = JsonDocument.Parse(joinText);
            Assert.AreEqual(position, body.RootElement.GetProperty("position").GetInt32());
            Assert.IsNotNull(newEntry);
            Assert.IsTrue(oldId < newEntry.Id, $"{oldId} < {newEntry.Id}");
            var promotedNewcomer = !cancelFirst && ownerSeats == 2;
            CollectionAssert.AreEqual(
                new[] { promotedNewcomer ? "promoted|1" : "waiting|-" },
                await PromotionOnCancelTests.QueueAsync(app, newEntry.Id));
            Assert.AreEqual(promotedNewcomer ? 4 : 5, await RemainingVisitsAsync(app, newcomer));
            Assert.AreEqual(promotedNewcomer ? 2L : 1L, await ActiveSeatsAsync(app, classId));
        }

        cancel.Dispose();
        join.Dispose();
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(true, 409, "promoted|2")]
    [DataRow(false, 201, "waiting|-")]
    public async Task Cancel_RacesWithEnroll_SeatsWithinCapacityAndChargedOnce(bool cancelFirst, int enrollStatus, string waiterState)
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var newcomer = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 2);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 1);
        var entry = await EntryAsync(app, waiter.Id, classId, 2);
        using var ownerClient = await app.CreateClientAsync(owner.Email);
        using var newcomerClient = await app.CreateClientAsync(newcomer.Email);

        await using var held = await HoldClassAsync(app, classId);
        var (cancel, enroll) = await InOrderAsync(
            held, cancelFirst, () => CancelAsync(ownerClient, ownerEnrollment), () => EnrollAsync(newcomerClient, classId, 1));

        await AssertCancelledAsync(cancel, ownerEnrollment, 1);
        Assert.AreEqual(enrollStatus, (int)enroll.StatusCode, await enroll.Content.ReadAsStringAsync());
        if (enrollStatus == 409)
        {
            await AssertConflictAsync(enroll, "capacity-exceeded");
        }

        CollectionAssert.AreEqual(new[] { waiterState }, await PromotionOnCancelTests.QueueAsync(app, entry));
        Assert.AreEqual(waiterState == "promoted|2" ? 3 : 5, await RemainingVisitsAsync(app, waiter));
        Assert.AreEqual(enrollStatus == 201 ? 4 : 5, await RemainingVisitsAsync(app, newcomer));
        Assert.AreEqual(enrollStatus == 201 ? 1L : 2L, await ActiveSeatsAsync(app, classId));
        cancel.Dispose();
        enroll.Dispose();
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Cancel_ParallelBatchWithEnrollsAndJoinsWithoutHeldLock_WithinCapacityAndVisitsBalanced()
    {
        await using var app = await TestApp.StartAsync();
        const int capacity = 4;
        var classId = await app.Data.ClassAsync(capacity: capacity);
        var paidBefore = new Dictionary<long, int>();
        var users = new List<TestUser>();
        var requests = new List<Func<Task<HttpResponseMessage>>>();
        var clients = new List<HttpClient>();

        for (var i = 0; i < capacity; i++)
        {
            var owner = await app.Data.UserAsync(remainingVisits: 5);
            var waiter = await app.Data.UserAsync(remainingVisits: 5);
            var enroller = await app.Data.UserAsync(remainingVisits: 5);
            var joiner = await app.Data.UserAsync(remainingVisits: 5);
            users.AddRange([owner, waiter, enroller, joiner]);
            var enrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 1);
            paidBefore[owner.Id] = 1;
            await EntryAsync(app, waiter.Id, classId, 1);
            var ownerClient = await app.CreateClientAsync(owner.Email);
            var enrollerClient = await app.CreateClientAsync(enroller.Email);
            var joinerClient = await app.CreateClientAsync(joiner.Email);
            clients.AddRange([ownerClient, enrollerClient, joinerClient]);
            requests.Add(() => CancelAsync(ownerClient, enrollment));
            requests.Add(() => EnrollAsync(enrollerClient, classId, 1));
            requests.Add(() => JoinAsync(joinerClient, classId, 1));
        }

        var responses = await Task.WhenAll(requests.Select(Task.Run));

        foreach (var response in responses)
        {
            var status = (int)response.StatusCode;
            Assert.IsTrue(status is 200 or 201 or 409, $"{status} {await response.Content.ReadAsStringAsync()}");
        }

        Assert.IsTrue(await ActiveSeatsAsync(app, classId) <= capacity);
        await using var context = app.CreateContext();
        foreach (var user in users)
        {
            var enrollments = await context.Enrollments.AsNoTracking().Where(x => x.UserId == user.Id).ToListAsync();
            var expected = 5 + paidBefore.GetValueOrDefault(user.Id) - enrollments.Sum(x => x.Seats) + enrollments.Sum(x => x.RefundedVisits ?? 0);
            Assert.AreEqual(expected, await RemainingVisitsAsync(app, user), $"user {user.Id}");
        }

        var promoted = await context.WaitlistEntries.AsNoTracking()
            .Where(x => x.ClassId == classId && x.Status == "promoted").Select(x => x.Id).ToArrayAsync();
        CollectionAssert.AreEqual(
            promoted.Select(_ => "promoted|1").ToArray(), await PromotionOnCancelTests.QueueAsync(app, promoted));
        DisposeAll(responses);
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    private static async Task<(HttpResponseMessage Cancel, HttpResponseMessage Other)> InOrderAsync(
        HeldLock held, bool cancelFirst, Func<Task<HttpResponseMessage>> cancel, Func<Task<HttpResponseMessage>> other)
    {
        var first = cancelFirst ? cancel() : other();
        Assert.AreEqual(1L, await held.WaitForBlockedCountAsync(1));
        var second = cancelFirst ? other() : cancel();
        Assert.AreEqual(2L, await held.WaitForBlockedCountAsync(2));
        await held.DisposeAsync();
        var firstResponse = await first;
        var secondResponse = await second;

        return cancelFirst ? (firstResponse, secondResponse) : (secondResponse, firstResponse);
    }

    private static Task<HttpResponseMessage> LeaveAsync(HttpClient client, long id)
    {
        return client.PostAsync($"/enrollments/waitlist/{id}/cancel", null);
    }

    private static Task<HeldLock> HoldClassAsync(TestApp app, long classId)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");
    }

    private static async Task<long> ActiveSeatsAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();

        return await context.Enrollments.Where(x => x.ClassId == classId && x.Status == "active").SumAsync(x => (long)x.Seats);
    }

    private static void DisposeAll(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
