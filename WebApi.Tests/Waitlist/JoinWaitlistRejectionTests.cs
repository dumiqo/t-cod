using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Idempotency;
using WebApi.Tests.Enrollments;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class JoinWaitlistRejectionTests
{
    [TestMethod]
    public async Task Join_ClassCancelled_Returns409ClassCancelledWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(capacity: 2, cancelled: true);
        var filler = await app.Data.UserAsync();
        await app.Data.EnrollmentAsync(filler.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 1);

        await EnrollApi.AssertConflictAsync(response, "class-cancelled");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task Join_ClockAtOrAfterStartsAt_Returns409ClassStartedWithoutTrace(long ticksAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(1);
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAtAsync(app, startsAt);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(ticksAfterStart));

        using var response = await JoinAsync(client, classId, 1);

        await EnrollApi.AssertConflictAsync(response, "class-started");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Join_ClockOneTickBeforeStartsAt_Returns201()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(1);
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAtAsync(app, startsAt);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(-1));

        using var response = await JoinAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        await using var context = app.CreateContext();
        var entry = await context.WaitlistEntries.SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(startsAt.AddTicks(-TimeSpan.TicksPerMicrosecond), entry.CreatedAt);
    }

    [TestMethod]
    [DataRow(5, 3, 2)]
    [DataRow(5, 3, 1)]
    [DataRow(5, 0, 5)]
    [DataRow(1, 0, 1)]
    public async Task Join_EnoughFreeSeats_Returns409SeatsAvailableWithoutTrace(int capacity, int taken, int seats)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: capacity);
        if (taken > 0)
        {
            await app.Data.EnrollmentAsync(other.Id, classId, taken);
        }

        var before = await EnrollApi.EnrollmentsSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, seats);

        await EnrollApi.AssertConflictAsync(response, "seats-available");
        await AssertNoTraceAsync(app, user, 10);
        CollectionAssert.AreEqual(before, await EnrollApi.EnrollmentsSnapshotAsync(app, classId));
    }

    [TestMethod]
    [DataRow(5, 3, 3)]
    [DataRow(1, 0, 2)]
    public async Task Join_OneFreeSeatShort_Returns201(int capacity, int taken, int seats)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: capacity);
        if (taken > 0)
        {
            await app.Data.EnrollmentAsync(other.Id, classId, taken);
        }

        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, seats);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        await AssertSingleWaitingAsync(app, user, classId, seats);
    }

    [TestMethod]
    public async Task Join_CancelledEnrollmentsOnClass_DoNotTakeSeats()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 3);
        await app.Data.EnrollmentAsync(other.Id, classId, 3, cancelled: true);
        await app.Data.EnrollmentAsync(other.Id, classId, 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 2);

        await EnrollApi.AssertConflictAsync(response, "seats-available");
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Join_OverfilledClass_Returns201()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 2);
        await app.Data.EnrollmentAsync(other.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        await AssertSingleWaitingAsync(app, user, classId, 1);
    }

    [TestMethod]
    public async Task Join_MaxSeatsOnMaxCapacityClassWithOneTaken_Returns201WithoutOverflow()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: int.MaxValue);
        await app.Data.EnrollmentAsync(other.Id, classId, 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, int.MaxValue);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        await AssertSingleWaitingAsync(app, user, classId, int.MaxValue);
    }

    [TestMethod]
    public async Task Join_SeatsAboveCapacity_Returns201()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(capacity: 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 3);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        await AssertSingleWaitingAsync(app, user, classId, 3);
    }

    [TestMethod]
    public async Task Join_AlreadyWaitingWithOtherSeats_Returns409AlreadyWaitingEntryUnchanged()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app, capacity: 5);
        using var client = await app.CreateClientAsync(user.Email);
        using var first = await JoinAsync(client, classId, 2);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        var before = await WaitlistSnapshotAsync(app, classId);
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        using var response = await JoinAsync(client, classId, 3);

        await EnrollApi.AssertConflictAsync(response, "already-waiting");
        CollectionAssert.AreEqual(before, await WaitlistSnapshotAsync(app, classId));
        await AssertSingleWaitingAsync(app, user, classId, 2);
        Assert.AreEqual(10, await EnrollApi.RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Join_AlreadyWaitingWithNewKey_KeyNotTakenAndUsableAfterLeaving()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await FullClassAsync(app, capacity: 5);
        using var client = await app.CreateClientAsync(user.Email);
        var keyB = Guid.NewGuid().ToString();
        using var first = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":2}}", Guid.NewGuid().ToString());
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstId = firstBody.RootElement.GetProperty("id").GetInt64();

        using var rejected = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":3}}", keyB);

        await EnrollApi.AssertConflictAsync(rejected, "already-waiting");
        var hashB = IdempotencyKeyHash.Compute(keyB);
        await using (var context = app.CreateContext())
        {
            Assert.AreEqual(1, await context.IdempotencyKeys.CountAsync(x => x.UserId == user.Id));
            Assert.IsFalse(await context.IdempotencyKeys.AnyAsync(x => x.KeyHash == hashB));
        }

        Assert.AreEqual(1, JoinedEvents(app).Count);
        Assert.AreEqual(2, EnrollApi.MetricSum(app, "club.waitlist.joined"));

        using (var left = await client.PostAsync($"/enrollments/waitlist/{firstId}/cancel", null))
        {
            Assert.AreEqual(HttpStatusCode.OK, left.StatusCode, await left.Content.ReadAsStringAsync());
        }

        using var retried = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":3}}", keyB);

        var text = await retried.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, retried.StatusCode, text);
        using var body = JsonDocument.Parse(text);
        var id = body.RootElement.GetProperty("id").GetInt64();
        Assert.AreNotEqual(firstId, id);
        Assert.AreEqual(3, body.RootElement.GetProperty("seats").GetInt32());
        await AssertSingleWaitingAsync(app, user, classId, 3);
        await using var after = app.CreateContext();
        var key = await after.IdempotencyKeys.SingleAsync(x => x.KeyHash == hashB);
        Assert.AreEqual(id, key.ResultId);
    }

    [TestMethod]
    public async Task Join_WaitingOnOtherClassAndOtherUserWaitingHere_Returns201()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var otherClassId = await FullClassAsync(app);
        await EntryAsync(app, user.Id, otherClassId, 1);
        await EntryAsync(app, other.Id, classId, 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(2, body.RootElement.GetProperty("position").GetInt32());
    }

    [TestMethod]
    public async Task Join_ActiveEnrollmentAndEnoughFreeSeats_Returns409SeatsAvailable()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(capacity: 5);
        await app.Data.EnrollmentAsync(user.Id, classId, 2);
        var before = await EnrollApi.EnrollmentsSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 3);

        await EnrollApi.AssertConflictAsync(response, "seats-available");
        CollectionAssert.AreEqual(before, await EnrollApi.EnrollmentsSnapshotAsync(app, classId));
        await using var context = app.CreateContext();
        Assert.IsFalse(await context.WaitlistEntries.AnyAsync(x => x.UserId == user.Id));
        Assert.AreEqual(10, await EnrollApi.RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Join_AfterLeaving_Returns201WithNewIdAtEndOfQueue()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var oldId = await EntryAsync(app, user.Id, classId, 1, "cancelled");
        await EntryAsync(app, other.Id, classId, 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var newId = body.RootElement.GetProperty("id").GetInt64();
        Assert.IsTrue(newId > oldId, $"{newId} > {oldId}");
        Assert.AreEqual(2, body.RootElement.GetProperty("position").GetInt32());
        await using var context = app.CreateContext();
        Assert.AreEqual("cancelled", await context.WaitlistEntries.Where(x => x.Id == oldId).Select(x => x.Status).SingleAsync());
    }

    [TestMethod]
    public async Task Join_AfterPromotion_Returns201()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 2);
        await app.Data.EnrollmentAsync(other.Id, classId, 1);
        await app.Data.EnrollmentAsync(user.Id, classId, 1);
        await EntryAsync(app, user.Id, classId, 1, "promoted");
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        await AssertSingleWaitingAsync(app, user, classId, 1);
    }

    [TestMethod]
    [DataRow(10, -1)]
    [DataRow(0, 30)]
    public async Task Join_PassExpiredOrExhausted_Returns201AndVisitsUnchanged(int visits, int validDays)
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(Now(app));
        var user = await app.Data.UserAsync(remainingVisits: visits, validUntil: today.AddDays(validDays));
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        await AssertSingleWaitingAsync(app, user, classId, 1);
        Assert.AreEqual(visits, await EnrollApi.RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(true, true, false, false, "class-cancelled")]
    [DataRow(true, false, true, false, "class-cancelled")]
    [DataRow(true, false, false, true, "class-cancelled")]
    [DataRow(false, true, true, false, "class-started")]
    [DataRow(false, true, false, true, "class-started")]
    [DataRow(false, false, true, true, "already-waiting")]
    [DataRow(true, true, true, true, "class-cancelled")]
    public async Task Join_SeveralReasons_Returns409WithFirstByPriority(
        bool cancelled, bool started, bool waiting, bool free, string reason)
    {
        await using var app = await TestApp.StartAsync();
        var now = Now(app);
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(
            capacity: free ? 10 : 2,
            startsAt: started ? now.AddHours(-1) : now.AddDays(1),
            cancelled: cancelled);
        await app.Data.EnrollmentAsync(other.Id, classId, 1);
        if (waiting)
        {
            await EntryAsync(app, user.Id, classId, 1);
        }

        var enrollmentsBefore = await EnrollApi.EnrollmentsSnapshotAsync(app, classId);
        var waitlistBefore = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 2);

        await EnrollApi.AssertConflictAsync(response, reason);
        CollectionAssert.AreEqual(enrollmentsBefore, await EnrollApi.EnrollmentsSnapshotAsync(app, classId));
        CollectionAssert.AreEqual(waitlistBefore, await WaitlistSnapshotAsync(app, classId));
        Assert.AreEqual(10, await EnrollApi.RemainingVisitsAsync(app, user));
    }

    private static async Task<long> FullClassAtAsync(TestApp app, DateTime startsAt)
    {
        var filler = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 2, startsAt: startsAt);
        await app.Data.EnrollmentAsync(filler.Id, classId, 2);

        return classId;
    }

    private static async Task AssertSingleWaitingAsync(TestApp app, TestUser user, long classId, int seats)
    {
        await using var context = app.CreateContext();
        var entry = await context.WaitlistEntries.SingleAsync(x => x.UserId == user.Id && x.Status == "waiting");
        Assert.AreEqual(classId, entry.ClassId);
        Assert.AreEqual(seats, entry.Seats);
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }
}
