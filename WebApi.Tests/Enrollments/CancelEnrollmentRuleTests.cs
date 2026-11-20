using System.Net;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class CancelEnrollmentRuleTests
{
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(-TimeSpan.TicksPerHour)]
    public async Task Cancel_TwoHoursOrMoreBeforeStart_RefundsAllVisits(long ticksFromBoundary)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt - TwoHours + TimeSpan.FromTicks(ticksFromBoundary));

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 3);
        var enrollment = await EnrollmentAsync(app, id);
        Assert.AreEqual("cancelled", enrollment.Status);
        Assert.AreEqual(3, enrollment.RefundedVisits);
        Assert.AreEqual(7, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_OneTickLessThanTwoHoursBeforeStart_FreesSeatsWithoutRefund()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt - TwoHours + TimeSpan.FromTicks(1));

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 0);
        var enrollment = await EnrollmentAsync(app, id);
        Assert.AreEqual("cancelled", enrollment.Status);
        Assert.AreEqual(startsAt - TwoHours, enrollment.CancelledAt);
        Assert.AreEqual(0, enrollment.RefundedVisits);
        Assert.AreEqual(4, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(TimeSpan.TicksPerHour)]
    [DataRow(1L)]
    public async Task Cancel_LessThanTwoHoursBeforeStart_FreesSeatsForAnotherClient(long ticksBeforeStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var owner = await app.Data.UserAsync(remainingVisits: 2);
        var other = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 3, startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        using var ownerClient = await app.CreateClientAsync(owner.Email);
        using var otherClient = await app.CreateClientAsync(other.Email);
        var now = startsAt.AddTicks(-ticksBeforeStart);
        app.Clock.SetUtcNow(now);

        using var response = await CancelAsync(ownerClient, id);
        using var enroll = await EnrollAsync(otherClient, classId, 3);

        await AssertCancelledAsync(response, id, 0);
        Assert.AreEqual(HttpStatusCode.Created, enroll.StatusCode, await enroll.Content.ReadAsStringAsync());
        var enrollment = await EnrollmentAsync(app, id);
        Assert.AreEqual(0, enrollment.RefundedVisits);
        Assert.AreEqual(now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond)), enrollment.CancelledAt);
        Assert.AreEqual(2, await RemainingVisitsAsync(app, owner));
        Assert.AreEqual(2, await RemainingVisitsAsync(app, other));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    [DataRow(TimeSpan.TicksPerDay)]
    public async Task Cancel_ClockAtOrAfterStartsAt_Returns409ClassStartedWithoutTrace(long ticksAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(1);
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        var snapshot = await SnapshotAsync(app, id);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(ticksAfterStart));

        using var response = await CancelAsync(client, id);

        await AssertConflictAsync(response, "class-started");
        await AssertNoCancelTraceAsync(app, id, user, 5);
        Assert.AreEqual(snapshot, await SnapshotAsync(app, id));
    }

    [TestMethod]
    public async Task Cancel_ClassStartedForOtherUsersEnrollment_Returns404()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(1);
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(startsAt: startsAt), 2);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt);

        using var response = await CancelAsync(client, id);

        await AssertNotFoundAsync(response);
        await AssertNoCancelTraceAsync(app, id, owner, 5);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task Cancel_AlreadyCancelledAfterStart_Returns200WithStoredRefund(int refundedVisits)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(1);
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2, cancelled: true, refundedVisits: refundedVisits);
        var snapshot = await SnapshotAsync(app, id);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt.AddMinutes(5));

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, refundedVisits);
        Assert.AreEqual(snapshot, await SnapshotAsync(app, id));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(TimeSpan.TicksPerHour)]
    [DataRow(1L)]
    public async Task Cancel_ClassCancelledByClubAfterCancellationWithoutRefund_RepeatReturnsZero(long ticksBeforeStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(-ticksBeforeStart));
        using var first = await CancelAsync(client, id);
        await AssertCancelledAsync(first, id, 0);
        var snapshot = await SnapshotAsync(app, id);
        await CancelClassAsync(app, classId);
        app.Clock.SetUtcNow(startsAt.AddHours(1));

        using var repeat = await CancelAsync(client, id);

        await AssertCancelledAsync(repeat, id, 0);
        Assert.AreEqual(snapshot, await SnapshotAsync(app, id));
        Assert.AreEqual(4, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(-TimeSpan.TicksPerDay)]
    [DataRow(-TimeSpan.TicksPerHour)]
    [DataRow(-1L)]
    [DataRow(0L)]
    [DataRow(1L)]
    [DataRow(TimeSpan.TicksPerDay)]
    public async Task Cancel_ClassCancelledByClub_RefundsAllVisitsAtAnyTime(long ticksAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(2);
        var user = await app.Data.UserAsync(remainingVisits: 1);
        var classId = await app.Data.ClassAsync(startsAt: startsAt, cancelled: true);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(ticksAfterStart));

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 3);
        var enrollment = await EnrollmentAsync(app, id);
        Assert.AreEqual("cancelled", enrollment.Status);
        Assert.AreEqual(3, enrollment.RefundedVisits);
        Assert.AreEqual(4, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_ExpiredPass_RefundsAllVisits()
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(Now(app));
        var user = await app.Data.UserAsync(remainingVisits: 6, validUntil: today.AddDays(-1));
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 2);
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_ExhaustedPass_RefundsAllVisits()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 0);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 5);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 5);
        Assert.AreEqual(5, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_RefundOverflowsRemainingVisits_Returns500WithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: int.MaxValue);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode, await response.Content.ReadAsStringAsync());
        await AssertNoCancelTraceAsync(app, id, user, int.MaxValue);
    }

    [TestMethod]
    public async Task Cancel_RefundUpToIntMax_Returns200()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 0);
        var classId = await app.Data.ClassAsync(capacity: int.MaxValue);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, int.MaxValue);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, int.MaxValue);
        Assert.AreEqual(int.MaxValue, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(int.MaxValue, (await EnrollmentAsync(app, id)).RefundedVisits);
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }

    private static async Task CancelClassAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();
        var updated = await context.Classes
            .Where(x => x.Id == classId)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.IsCancelled, true));
        Assert.AreEqual(1, updated);
    }
}
