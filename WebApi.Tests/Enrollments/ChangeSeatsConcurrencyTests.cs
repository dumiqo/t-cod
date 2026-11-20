using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.ChangeSeatsApi;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class ChangeSeatsConcurrencyTests
{
    [TestMethod]
    public async Task ChangeSeats_EnrollmentCancelledWhileWaiting_Returns409EnrollmentCancelled()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 7);
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldClassAsync(app, classId);
        var request = ChangeSeatsAsync(client, id, 5);
        var blocked = await held.WaitForBlockedCountAsync(1);
        await held.ExecuteAsync($"UPDATE enrollments SET status = 'cancelled', cancelled_at = now(), refunded_visits = 2 WHERE id = {id}");
        await held.CommitAsync();
        using var response = await request;

        Assert.AreEqual(1L, blocked);
        await AssertConflictAsync(response, "enrollment-cancelled");
        var enrollment = await EnrollmentAsync(app, id);
        Assert.AreEqual(2, enrollment.Seats);
        Assert.AreEqual("cancelled", enrollment.Status);
        Assert.AreEqual(2, enrollment.RefundedVisits);
        Assert.AreEqual(7, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ChangeSeats_ParallelDecreaseAndCancelOfOneEnrollment_RefundOriginalSeatsOnce()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 5);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldClassAsync(app, classId);
        var change = ChangeSeatsAsync(client, id, 2);
        var cancel = CancelAsync(client, id);
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        using var changed = await change;
        using var cancelled = await cancel;

        Assert.AreEqual(2L, blocked);
        if (changed.StatusCode == HttpStatusCode.OK)
        {
            await AssertSeatsAsync(changed, 2);
            await AssertCancelledAsync(cancelled, id, 2);
            Assert.AreEqual("3", One(DecreasedEvents(app)).GetStructuredStateValue("RefundedVisits"));
            Assert.AreEqual("2", One(RefundedEvents(app)).GetStructuredStateValue("RefundedVisits"));
        }
        else
        {
            await AssertConflictAsync(changed, "enrollment-cancelled");
            await AssertCancelledAsync(cancelled, id, 5);
            Assert.AreEqual(0, DecreasedEvents(app).Count);
            Assert.AreEqual("5", One(RefundedEvents(app)).GetStructuredStateValue("RefundedVisits"));
        }

        Assert.AreEqual(5L, MetricSum(app, "club.visits.refunded"));

        Assert.AreEqual("cancelled", (await EnrollmentAsync(app, id)).Status);
        Assert.AreEqual(9, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ChangeSeats_IncreaseRacesEnrollForLastSeat_OneSucceedsOtherCapacityExceeded()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var other = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 5);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 4);
        using var client = await app.CreateClientAsync(user.Email);
        using var otherClient = await app.CreateClientAsync(other.Email);

        await using var held = await HoldClassAsync(app, classId);
        var change = ChangeSeatsAsync(client, id, 5);
        var enroll = EnrollAsync(otherClient, classId, 1);
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        using var changed = await change;
        using var enrolled = await enroll;

        Assert.AreEqual(2L, blocked);
        if (changed.StatusCode == HttpStatusCode.OK)
        {
            await AssertSeatsAsync(changed, 5);
            await AssertConflictAsync(enrolled, "capacity-exceeded");
            Assert.AreEqual(4, await RemainingVisitsAsync(app, user));
            Assert.AreEqual(5, await RemainingVisitsAsync(app, other));
            Assert.AreEqual("1", One(IncreasedEvents(app)).GetStructuredStateValue("ChargedVisits"));
            Assert.AreEqual(0, CreatedEvents(app).Count);
        }
        else
        {
            await AssertConflictAsync(changed, "capacity-exceeded");
            Assert.AreEqual(HttpStatusCode.Created, enrolled.StatusCode, await enrolled.Content.ReadAsStringAsync());
            await AssertNoChangeTraceAsync(app, user, id, 4, 5);
            Assert.AreEqual(4, await RemainingVisitsAsync(app, other));
            Assert.AreEqual(1, CreatedEvents(app).Count);
        }

        Assert.AreEqual(5, await ActiveSeatsAsync(app, classId));
        Assert.AreEqual(1L, MetricSum(app, "club.visits.charged"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task ChangeSeats_IncreaseRacesForLastVisitOnOtherClass_OneSucceedsOtherPassExhausted(bool otherIsIncrease)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 1);
        var first = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        var secondClass = await app.Data.ClassAsync();
        var second = await app.Data.EnrollmentAsync(user.Id, secondClass, 1);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldPassAsync(app, user);
        var requests = new[]
        {
            ChangeSeatsAsync(client, first, 2),
            otherIsIncrease ? ChangeSeatsAsync(client, second, 2) : EnrollAsync(client, secondClass, 1)
        };
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        var responses = await Task.WhenAll(requests);

        Assert.AreEqual(2L, blocked);
        var success = otherIsIncrease ? HttpStatusCode.OK : HttpStatusCode.Created;
        Assert.AreEqual(
            1,
            (responses[0].StatusCode == HttpStatusCode.OK ? 1 : 0) + (responses[1].StatusCode == success ? 1 : 0));
        foreach (var response in responses.Where(x => x.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created)))
        {
            await AssertConflictAsync(response, "pass-exhausted");
        }

        Assert.AreEqual(0, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(responses[0].StatusCode == HttpStatusCode.OK ? 2 : 1, (await EnrollmentAsync(app, first)).Seats);
        Assert.AreEqual(3, await ActiveSeatsOfUserAsync(app, user));
        long? winner = responses[0].StatusCode == HttpStatusCode.OK ? first : otherIsIncrease ? second : null;
        CollectionAssert.AreEqual(
            winner is null ? Array.Empty<string>() : new[] { winner.Value.ToString() },
            IncreasedEvents(app).Select(x => x.GetStructuredStateValue("EnrollmentId")).ToArray());
        Assert.AreEqual(winner is null ? 1 : 0, CreatedEvents(app).Count);
        Assert.AreEqual(1L, MetricSum(app, "club.visits.charged"));
        DisposeAll(responses);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ChangeSeats_DecreaseRacesCancelOnOtherClass_BothRefundsReachPass()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 0);
        var first = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 5);
        var second = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldPassAsync(app, user);
        var change = ChangeSeatsAsync(client, first, 2);
        var cancel = CancelAsync(client, second);
        var blocked = await held.WaitForBlockedCountAsync(2);
        await held.DisposeAsync();
        using var changed = await change;
        using var cancelled = await cancel;

        Assert.AreEqual(2L, blocked);
        await AssertSeatsAsync(changed, 2);
        await AssertCancelledAsync(cancelled, second, 2);
        Assert.AreEqual(2, (await EnrollmentAsync(app, first)).Seats);
        Assert.AreEqual(5, await RemainingVisitsAsync(app, user));
        Assert.AreEqual("3", One(DecreasedEvents(app)).GetStructuredStateValue("RefundedVisits"));
        Assert.AreEqual("2", One(RefundedEvents(app)).GetStructuredStateValue("RefundedVisits"));
        Assert.AreEqual(5L, MetricSum(app, "club.visits.refunded"));
    }

    [TestMethod]
    [DataRow(false, 3)]
    [DataRow(true, 3)]
    [DataRow(false, 1)]
    [DataRow(true, 1)]
    public async Task ChangeSeats_ClockReachesStartWhileWaiting_Returns409ClassStartedWithoutTrace(bool holdPass, int seats)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = holdPass ? await HoldPassAsync(app, user) : await HoldClassAsync(app, classId);
        var request = ChangeSeatsAsync(client, id, seats);
        var blocked = await held.WaitForBlockedCountAsync(1);
        app.Clock.SetUtcNow(startsAt);
        await held.DisposeAsync();
        using var response = await request;

        Assert.AreEqual(1L, blocked);
        await AssertConflictAsync(response, "class-started");
        await AssertNoChangeTraceAsync(app, user, id, 2, 4);
    }

    [TestMethod]
    public async Task ChangeSeats_PassExpiresWhileWaiting_Returns409PassExpiredWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var validUntil = DateOnly.FromDateTime(Now(app)).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4, validUntil: validUntil);
        var classId = await app.Data.ClassAsync(startsAt: Now(app).AddDays(5));
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldPassAsync(app, user);
        var request = ChangeSeatsAsync(client, id, 3);
        var blocked = await held.WaitForBlockedCountAsync(1);
        app.Clock.SetUtcNow(new DateTimeOffset(validUntil.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        await held.DisposeAsync();
        using var response = await request;

        Assert.AreEqual(1L, blocked);
        await AssertConflictAsync(response, "pass-expired");
        await AssertNoChangeTraceAsync(app, user, id, 2, 4);
    }

    [TestMethod]
    public async Task ChangeSeats_CancelledEnrollmentWhileClassLocked_Returns409WithoutWaiting()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2, cancelled: true);
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HoldClassAsync(app, classId);

        using var response = await ChangeSeatsAsync(client, id, 3);

        await AssertConflictAsync(response, "enrollment-cancelled");
        Assert.AreEqual(4, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangeSeats_LockTimeout_Returns503WithoutTrace(bool holdPass)
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = holdPass ? await HoldPassAsync(app, user) : await HoldClassAsync(app, classId);

        using var response = await ChangeSeatsAsync(client, id, 3);

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, text);
        Assert.AreEqual("1", response.Headers.GetValues("Retry-After").Single());
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual("lock-timeout", body.RootElement.GetProperty("reason").GetString());
        await AssertNoChangeTraceAsync(app, user, id, 2, 4);
    }

    private static async Task AssertSeatsAsync(HttpResponseMessage response, int seats)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual(seats, body.RootElement.GetProperty("seats").GetInt32());
        Assert.AreEqual("active", body.RootElement.GetProperty("status").GetString());
    }

    private static async Task<int> ActiveSeatsAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();

        return await context.Enrollments.Where(x => x.ClassId == classId && x.Status == "active").SumAsync(x => x.Seats);
    }

    private static async Task<int> ActiveSeatsOfUserAsync(TestApp app, TestUser user)
    {
        await using var context = app.CreateContext();

        return await context.Enrollments.Where(x => x.UserId == user.Id && x.Status == "active").SumAsync(x => x.Seats);
    }

    private static T One<T>(IReadOnlyList<T> items)
    {
        Assert.AreEqual(1, items.Count);

        return items[0];
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }

    private static Task<HeldLock> HoldClassAsync(TestApp app, long classId)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");
    }

    private static Task<HeldLock> HoldPassAsync(TestApp app, TestUser user)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM passes WHERE user_id = {user.Id} FOR UPDATE");
    }

    private static void DisposeAll(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
