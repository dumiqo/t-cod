using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.ChangeSeatsApi;
using static WebApi.Tests.Enrollments.EnrollApi;
using static WebApi.Tests.Waitlist.PromotionOnCancelTests;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class PromotionObservabilityTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Cancel_QueueWithPromotionsAndSkips_LogsCancelThenQueueInIdOrder(bool refund)
    {
        await using var app = await TestApp.StartAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var startsAt = now.AddDays(1);
        var today = DateOnly.FromDateTime(now);
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var expired = await app.Data.UserAsync(remainingVisits: 5, validUntil: today.AddDays(-1));
        var tooBig = await app.Data.UserAsync(remainingVisits: 5);
        var exhausted = await app.Data.UserAsync(remainingVisits: 1);
        var twoSeats = await app.Data.UserAsync(remainingVisits: 5);
        var oneSeat = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(capacity: 3, startsAt: startsAt);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        var expiredEntry = await EntryAsync(app, expired.Id, classId, 1);
        var tooBigEntry = await EntryAsync(app, tooBig.Id, classId, 4);
        var ownEntry = await EntryAsync(app, owner.Id, classId, 2);
        var exhaustedEntry = await EntryAsync(app, exhausted.Id, classId, 2);
        var twoSeatsEntry = await EntryAsync(app, twoSeats.Id, classId, 2);
        var oneSeatEntry = await EntryAsync(app, oneSeat.Id, classId, 1);
        using var client = await app.CreateClientAsync(owner.Email);
        app.Clock.SetUtcNow(refund ? startsAt.AddHours(-2) : startsAt.AddHours(-2).AddTicks(1));

        using var response = await CancelAsync(client, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, refund ? 3 : 0);
        CollectionAssert.AreEqual(
            new[] { "waiting|-", "waiting|-", "waiting|-", "waiting|-", "promoted|2", "promoted|1" },
            await QueueAsync(app, expiredEntry, tooBigEntry, ownEntry, exhaustedEntry, twoSeatsEntry, oneSeatEntry));
        var events = OperationEvents(app);
        Assert.AreEqual(6, events.Count);
        Assert.AreEqual(refund ? 2002 : 2003, events[0].Id.Id);
        Assert.AreEqual(ownerEnrollment.ToString(), events[0].GetStructuredStateValue("EnrollmentId"));
        AssertSkipped(events[1], expiredEntry, classId, 1, expired, "pass-expired");
        AssertSkipped(events[2], ownEntry, classId, 2, owner, "own-release");
        AssertSkipped(events[3], exhaustedEntry, classId, 2, exhausted, "pass-exhausted");
        AssertPromoted(events[4], twoSeatsEntry, classId, 2, twoSeats, 3);
        AssertPromoted(events[5], oneSeatEntry, classId, 1, oneSeat, 3);
        CollectionAssert.AreEqual(new long[] { 2, 1 }, Values(app, "club.waitlist.promoted"));
        CollectionAssert.AreEqual(new long[] { 2, 1 }, Values(app, "club.visits.charged"));
        CollectionAssert.AreEqual(refund ? new long[] { 3 } : Array.Empty<long>(), Values(app, "club.visits.refunded"));
        Assert.AreEqual(0, Values(app, "club.enrollments.created").Length);
        Assert.AreEqual(0, app.Logs.GetSnapshot().Count(x => x.Id.Id is 1001 or 1002 or 2001));
    }

    [TestMethod]
    public async Task ChangeSeats_DecreasePromotesWaiter_LogsDecreaseThenPromotion()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 3);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        var entry = await EntryAsync(app, waiter.Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await ChangeSeatsAsync(client, ownerEnrollment, 1);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "promoted|2" }, await QueueAsync(app, entry));
        var events = OperationEvents(app);
        Assert.AreEqual(2, events.Count);
        Assert.AreEqual(2005, events[0].Id.Id);
        Assert.AreEqual(ownerEnrollment.ToString(), events[0].GetStructuredStateValue("EnrollmentId"));
        AssertPromoted(events[1], entry, classId, 2, waiter, 3);
        CollectionAssert.AreEqual(new long[] { 2 }, Values(app, "club.waitlist.promoted"));
        CollectionAssert.AreEqual(new long[] { 2 }, Values(app, "club.visits.charged"));
        CollectionAssert.AreEqual(new long[] { 2 }, Values(app, "club.visits.refunded"));
        Assert.AreEqual(0, Values(app, "club.enrollments.created").Length);
    }

    [TestMethod]
    public async Task Cancel_OnlyEntryDoesNotFit_NoPromotionEventsOrCounts()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 2);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        var entry = await EntryAsync(app, waiter.Id, classId, 3);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await CancelAsync(client, ownerEnrollment);

        await AssertCancelledAsync(response, ownerEnrollment, 2);
        CollectionAssert.AreEqual(new[] { "waiting|-" }, await QueueAsync(app, entry));
        CollectionAssert.AreEqual(new[] { 2002 }, OperationEvents(app).Select(x => x.Id.Id).ToArray());
        Assert.AreEqual(0, Values(app, "club.waitlist.promoted").Length);
        Assert.AreEqual(0, Values(app, "club.visits.charged").Length);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Promotion_WaiterPassLocked_Returns503WithoutEvents(bool decrease)
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 3);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        var entry = await EntryAsync(app, waiter.Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);
        await using var held = await HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM passes WHERE user_id = {waiter.Id} FOR UPDATE");

        using var response = decrease ? await ChangeSeatsAsync(client, ownerEnrollment, 1) : await CancelAsync(client, ownerEnrollment);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, await response.Content.ReadAsStringAsync());
        await held.DisposeAsync();
        await AssertNoPromotionTraceAsync(app, entry, owner, waiter);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Promotion_OwnRefundOverflowsPass_Returns500WithoutEvents(bool decrease)
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: int.MaxValue - 1);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 3);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        var entry = await EntryAsync(app, waiter.Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = decrease ? await ChangeSeatsAsync(client, ownerEnrollment, 1) : await CancelAsync(client, ownerEnrollment);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode, await response.Content.ReadAsStringAsync());
        await AssertNoPromotionTraceAsync(app, entry, owner, waiter, int.MaxValue - 1);
    }

    private static async Task AssertNoPromotionTraceAsync(TestApp app, long entry, TestUser owner, TestUser waiter, int ownerVisits = 5)
    {
        CollectionAssert.AreEqual(new[] { "waiting|-" }, await QueueAsync(app, entry));
        await using (var context = app.CreateContext())
        {
            Assert.IsFalse(await context.Enrollments.AnyAsync(x => x.Id == entry));
        }

        Assert.AreEqual(5, await RemainingVisitsAsync(app, waiter));
        Assert.AreEqual(ownerVisits, await RemainingVisitsAsync(app, owner));
        Assert.AreEqual(0, OperationEvents(app).Count);
        Assert.AreEqual(0, Values(app, "club.waitlist.promoted").Length);
        Assert.AreEqual(0, Values(app, "club.visits.charged").Length);
        Assert.AreEqual(0, Values(app, "club.visits.refunded").Length);
    }

    private static void AssertPromoted(FakeLogRecord record, long entry, long classId, int seats, TestUser user, int remaining)
    {
        Assert.AreEqual(3003, record.Id.Id);
        Assert.AreEqual("WaitlistPromoted", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual(entry.ToString(), record.GetStructuredStateValue("WaitlistId"));
        Assert.AreEqual(entry.ToString(), record.GetStructuredStateValue("EnrollmentId"));
        Assert.AreEqual(classId.ToString(), record.GetStructuredStateValue("ClassId"));
        Assert.AreEqual(seats.ToString(), record.GetStructuredStateValue("Seats"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        Assert.AreEqual(remaining.ToString(), record.GetStructuredStateValue("RemainingVisits"));
    }

    private static void AssertSkipped(FakeLogRecord record, long entry, long classId, int seats, TestUser user, string reason)
    {
        Assert.AreEqual(3004, record.Id.Id);
        Assert.AreEqual("WaitlistPromotionSkipped", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual(entry.ToString(), record.GetStructuredStateValue("WaitlistId"));
        Assert.AreEqual(classId.ToString(), record.GetStructuredStateValue("ClassId"));
        Assert.AreEqual(seats.ToString(), record.GetStructuredStateValue("Seats"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        Assert.AreEqual(reason, record.GetStructuredStateValue("Reason"));
    }

    private static IReadOnlyList<FakeLogRecord> OperationEvents(TestApp app)
    {
        return app.Logs.GetSnapshot().Where(x => x.Id.Id is 2002 or 2003 or 2005 or 3003 or 3004).ToList();
    }

    private static long[] Values(TestApp app, string instrument)
    {
        return app.Metrics(instrument).GetMeasurementSnapshot().Select(x => x.Value).ToArray();
    }
}
