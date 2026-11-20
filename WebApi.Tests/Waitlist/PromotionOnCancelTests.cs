using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.EnrollApi;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class PromotionOnCancelTests
{
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    [TestMethod]
    public async Task Cancel_WaitingEntryFits_BecomesEnrollmentWithEntryId()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 2);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        using var ownerClient = await app.CreateClientAsync(owner.Email);
        using var waiterClient = await app.CreateClientAsync(waiter.Email);
        var json = $"{{\"classId\":{classId},\"seats\":2}}";
        using var join = await WaitlistApi.PostAsync(waiterClient, json, "key-1");
        Assert.AreEqual(HttpStatusCode.Created, join.StatusCode, await join.Content.ReadAsStringAsync());
        var id = JsonDocument.Parse(await join.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        using var before = await waiterClient.GetAsync($"/enrollments/{id}");
        await AssertNotFoundAsync(before);
        app.Clock.Advance(TimeSpan.FromTicks(1234567));
        var now = Truncated(Now(app));

        using var response = await CancelAsync(ownerClient, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, 2);
        using var card = await waiterClient.GetAsync($"/enrollments/{id}");
        var cardText = await card.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, card.StatusCode, cardText);
        var root = JsonDocument.Parse(cardText).RootElement;
        Assert.AreEqual(id, root.GetProperty("id").GetInt64());
        Assert.AreEqual("active", root.GetProperty("status").GetString());
        Assert.AreEqual(2, root.GetProperty("seats").GetInt32());
        Assert.AreEqual(now, root.GetProperty("createdAt").GetDateTime());
        Assert.AreEqual(3, root.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual($"{id}|{waiter.Id}|{classId}|2|active|{now:O}||", await SnapshotAsync(app, id));
        await using (var context = app.CreateContext())
        {
            var entry = await context.WaitlistEntries.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.AreEqual("promoted", entry.Status);
            Assert.IsNull(entry.CancelledAt);
        }

        Assert.AreEqual(3, await RemainingVisitsAsync(app, waiter));
        Assert.AreEqual(7, await RemainingVisitsAsync(app, owner));
        using var replay = await WaitlistApi.PostAsync(waiterClient, json, "key-1");
        Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode);
        Assert.AreEqual(id, JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64());
        using var leave = await waiterClient.PostAsync($"/enrollments/waitlist/{id}/cancel", null);
        await AssertConflictAsync(leave, "already-promoted");
    }

    [TestMethod]
    public async Task Cancel_QueueWithMixedSizes_HandsOutAllFreeSeatsInIdOrderSkippingWhatDoesNotFit()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var filler = await app.Data.UserAsync();
        var users = new[] { await app.Data.UserAsync(), await app.Data.UserAsync(), await app.Data.UserAsync(), await app.Data.UserAsync() };
        var classId = await app.Data.ClassAsync(capacity: 5);
        await app.Data.EnrollmentAsync(filler.Id, classId, 1);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        var tooBig = await EntryAsync(app, users[0].Id, classId, 6);
        var fits = await EntryAsync(app, users[1].Id, classId, 3);
        var noLongerFits = await EntryAsync(app, users[2].Id, classId, 2);
        var last = await EntryAsync(app, users[3].Id, classId, 1);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await CancelAsync(client, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, 2);
        CollectionAssert.AreEqual(
            new[] { "waiting|-", "promoted|3", "waiting|-", "promoted|1" },
            await QueueAsync(app, tooBig, fits, noLongerFits, last));
        CollectionAssert.AreEqual(new[] { 10, 7, 10, 9 }, await RemainingAsync(app, users));
        Assert.AreEqual(5, await ActiveSeatsAsync(app, classId));
    }

    [TestMethod]
    [DataRow(2, "promoted|2", "waiting|-", 0)]
    [DataRow(1, "waiting|-", "promoted|2", 1)]
    [DataRow(0, "waiting|-", "promoted|2", 0)]
    public async Task Cancel_FirstWaiterVisits_SkippedWhenFewerThanSeats(int remainingVisits, string first, string second, int firstRemaining)
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var users = new[] { await app.Data.UserAsync(remainingVisits: remainingVisits), await app.Data.UserAsync(remainingVisits: 5) };
        var classId = await app.Data.ClassAsync(capacity: 2);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        var firstId = await EntryAsync(app, users[0].Id, classId, 2);
        var secondId = await EntryAsync(app, users[1].Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await CancelAsync(client, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, 2);
        CollectionAssert.AreEqual(new[] { first, second }, await QueueAsync(app, firstId, secondId));
        CollectionAssert.AreEqual(new[] { firstRemaining, second == "waiting|-" ? 5 : 3 }, await RemainingAsync(app, users));
    }

    [TestMethod]
    [DataRow(0L, "promoted|2", "waiting|-")]
    [DataRow(1L, "waiting|-", "promoted|2")]
    public async Task Cancel_FirstWaiterValidUntil_LastDayStillValid(long ticksAfterLastDay, string first, string second)
    {
        await using var app = await TestApp.StartAsync();
        var validUntil = DateOnly.FromDateTime(Now(app)).AddDays(1);
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var users = new[] { await app.Data.UserAsync(remainingVisits: 5, validUntil: validUntil), await app.Data.UserAsync(remainingVisits: 5) };
        var classId = await app.Data.ClassAsync(capacity: 2, startsAt: Now(app).AddDays(5));
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        var firstId = await EntryAsync(app, users[0].Id, classId, 2);
        var secondId = await EntryAsync(app, users[1].Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);
        app.Clock.SetUtcNow(validUntil.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddTicks(ticksAfterLastDay - 1));

        using var response = await CancelAsync(client, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, 2);
        CollectionAssert.AreEqual(new[] { first, second }, await QueueAsync(app, firstId, secondId));
        CollectionAssert.AreEqual(
            new[] { first == "waiting|-" ? 5 : 3, second == "waiting|-" ? 5 : 3 },
            await RemainingAsync(app, users));
    }

    [TestMethod]
    public async Task Cancel_WaiterWithoutPass_Returns500AndRollsBack()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 2);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        var entry = await EntryAsync(app, waiter.Id, classId, 2);
        await using (var context = app.CreateContext())
        {
            await context.Passes.Where(x => x.UserId == waiter.Id).ExecuteDeleteAsync();
        }

        var snapshot = await SnapshotAsync(app, ownerEnrollment);
        var queue = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await CancelAsync(client, ownerEnrollment);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(snapshot, await SnapshotAsync(app, ownerEnrollment));
        CollectionAssert.AreEqual(queue, await WaitlistSnapshotAsync(app, classId));
        CollectionAssert.AreEqual(new[] { "waiting|-" }, await QueueAsync(app, entry));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, owner));
    }

    [TestMethod]
    public async Task Cancel_ClassCancelledByClub_QueueNotPromoted()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 2, cancelled: true);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        var entry = await EntryAsync(app, waiter.Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await CancelAsync(client, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, 2);
        CollectionAssert.AreEqual(new[] { "waiting|-" }, await QueueAsync(app, entry));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, waiter));
        Assert.AreEqual(7, await RemainingVisitsAsync(app, owner));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task Cancel_ClassStarted_Returns409AndQueueUnchanged(long ticksAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(1);
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 2, startsAt: startsAt);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        await EntryAsync(app, waiter.Id, classId, 2);
        var snapshot = await SnapshotAsync(app, ownerEnrollment);
        var queue = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(owner.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(ticksAfterStart));

        using var response = await CancelAsync(client, ownerEnrollment);

        await AssertConflictAsync(response, "class-started");
        Assert.AreEqual(snapshot, await SnapshotAsync(app, ownerEnrollment));
        CollectionAssert.AreEqual(queue, await WaitlistSnapshotAsync(app, classId));
        Assert.AreEqual(1, (await EnrollmentsSnapshotAsync(app, classId)).Length);
        Assert.AreEqual(5, await RemainingVisitsAsync(app, owner));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, waiter));
    }

    [TestMethod]
    [DataRow(0L, 2)]
    [DataRow(1L, 0)]
    public async Task Cancel_AroundTwoHoursBeforeStart_PromotesAndChargesWaiter(long ticksPastBoundary, int refundedVisits)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 2, startsAt: startsAt);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        var entry = await EntryAsync(app, waiter.Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);
        app.Clock.SetUtcNow(startsAt - TwoHours + TimeSpan.FromTicks(ticksPastBoundary));

        using var response = await CancelAsync(client, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, refundedVisits);
        CollectionAssert.AreEqual(new[] { "promoted|2" }, await QueueAsync(app, entry));
        Assert.AreEqual(3, await RemainingVisitsAsync(app, waiter));
        Assert.AreEqual(5 + refundedVisits, await RemainingVisitsAsync(app, owner));
    }

    [TestMethod]
    [DataRow(0L, 2)]
    [DataRow(1L, 0)]
    public async Task Cancel_OwnWaitingEntry_SkippedAndSeatsGoToNext(long ticksPastBoundary, int refundedVisits)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 2, startsAt: startsAt);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        var own = await EntryAsync(app, owner.Id, classId, 2);
        var next = await EntryAsync(app, waiter.Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);
        app.Clock.SetUtcNow(startsAt - TwoHours + TimeSpan.FromTicks(ticksPastBoundary));

        using var response = await CancelAsync(client, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, refundedVisits);
        CollectionAssert.AreEqual(new[] { "waiting|-", "promoted|2" }, await QueueAsync(app, own, next));
        Assert.AreEqual(5 + refundedVisits, await RemainingVisitsAsync(app, owner));
        Assert.AreEqual(3, await RemainingVisitsAsync(app, waiter));
    }

    [TestMethod]
    public async Task Cancel_OnlyOwnWaitingEntry_SeatsStayFreeUntilOtherClientCancels()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var other = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 4);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        var otherEnrollment = await app.Data.EnrollmentAsync(other.Id, classId, 2);
        var own = await EntryAsync(app, owner.Id, classId, 2);
        using var ownerClient = await app.CreateClientAsync(owner.Email);
        using var otherClient = await app.CreateClientAsync(other.Email);

        using var response = await CancelAsync(ownerClient, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, 2);
        CollectionAssert.AreEqual(new[] { "waiting|-" }, await QueueAsync(app, own));
        Assert.AreEqual(2, await ActiveSeatsAsync(app, classId));
        Assert.AreEqual(7, await RemainingVisitsAsync(app, owner));

        using var otherResponse = await CancelAsync(otherClient, otherEnrollment);

        await AssertCancelledAsync(otherResponse, otherEnrollment, 2);
        CollectionAssert.AreEqual(new[] { "promoted|2" }, await QueueAsync(app, own));
        Assert.AreEqual(2, await ActiveSeatsAsync(app, classId));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, owner));
        Assert.AreEqual(7, await RemainingVisitsAsync(app, other));
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public async Task Cancel_ClassStillFullOrOverfull_NobodyPromoted(int fillerSeats)
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var filler = await app.Data.UserAsync();
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 2);
        await app.Data.EnrollmentAsync(filler.Id, classId, fillerSeats);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 1);
        var entry = await EntryAsync(app, waiter.Id, classId, 1);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await CancelAsync(client, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, 1);
        CollectionAssert.AreEqual(new[] { "waiting|-" }, await QueueAsync(app, entry));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, waiter));
    }

    [TestMethod]
    public async Task Cancel_AlreadyCancelledWithFreeSeats_QueueUntouched()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 2);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2, cancelled: true, refundedVisits: 2);
        var entry = await EntryAsync(app, waiter.Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await CancelAsync(client, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, 2);
        CollectionAssert.AreEqual(new[] { "waiting|-" }, await QueueAsync(app, entry));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, waiter));
    }

    private static async Task<string[]> QueueAsync(TestApp app, params long[] ids)
    {
        await using var context = app.CreateContext();
        var entries = await context.WaitlistEntries.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var enrollments = await context.Enrollments.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);

        return ids.Select(id =>
        {
            var entry = entries[id];

            if (!enrollments.TryGetValue(id, out var enrollment))
            {
                return $"{entry.Status}|-";
            }

            Assert.AreEqual($"{entry.UserId}|{entry.ClassId}|active", $"{enrollment.UserId}|{enrollment.ClassId}|{enrollment.Status}");
            Assert.IsNull(entry.CancelledAt);

            return $"{entry.Status}|{enrollment.Seats}";
        }).ToArray();
    }

    private static async Task<int[]> RemainingAsync(TestApp app, TestUser[] users)
    {
        var result = new int[users.Length];

        for (var i = 0; i < users.Length; i++)
        {
            result[i] = await RemainingVisitsAsync(app, users[i]);
        }

        return result;
    }

    private static async Task<long> ActiveSeatsAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();

        return await context.Enrollments.Where(x => x.ClassId == classId && x.Status == "active").SumAsync(x => (long)x.Seats);
    }

    private static DateTime Truncated(DateTime value)
    {
        return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }
}
