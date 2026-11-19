using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class EnrollRejectionTests
{
    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    [DataRow(TimeSpan.TicksPerHour)]
    public async Task Enroll_ClockAtOrAfterStartsAt_Returns409ClassStartedWithoutTrace(long ticksAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(1);
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(ticksAfterStart));

        using var response = await EnrollAsync(client, classId, 1);

        await AssertConflictAsync(response, "class-started");
        await AssertNoTraceAsync(app, user, 10);
        await AssertNoEnrollmentsAsync(app, classId);
    }

    [TestMethod]
    public async Task Enroll_ClockOneTickBeforeStartsAt_Returns201()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(1);
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(-1));

        using var response = await EnrollAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        await using var context = app.CreateContext();
        var enrollment = await context.Enrollments.SingleAsync(x => x.UserId == user.Id);
        Assert.AreEqual(startsAt.AddTicks(-TimeSpan.TicksPerMicrosecond), enrollment.CreatedAt);
        Assert.AreEqual(9, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_ClockAtNextDayAfterValidUntil_Returns409PassExpiredWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(Now(app));
        var user = await app.Data.UserAsync(remainingVisits: 10, validUntil: today);
        var classId = await app.Data.ClassAsync(startsAt: Now(app).AddDays(10));
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        using var response = await EnrollAsync(client, classId, 1);

        await AssertConflictAsync(response, "pass-expired");
        await AssertNoTraceAsync(app, user, 10);
        await AssertNoEnrollmentsAsync(app, classId);
    }

    [TestMethod]
    public async Task Enroll_PassExpiredYesterday_Returns409PassExpiredWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(Now(app));
        var user = await app.Data.UserAsync(remainingVisits: 10, validUntil: today.AddDays(-1));
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, 1);

        await AssertConflictAsync(response, "pass-expired");
        await AssertNoTraceAsync(app, user, 10);
        await AssertNoEnrollmentsAsync(app, classId);
    }

    [TestMethod]
    public async Task Enroll_ClockAtLastTickOfValidUntil_Returns201()
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(Now(app));
        var user = await app.Data.UserAsync(remainingVisits: 10, validUntil: today);
        var classId = await app.Data.ClassAsync(startsAt: Now(app).AddDays(10));
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(today.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));

        using var response = await EnrollAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(9, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_PassEndsTodayClassInAMonth_Returns201()
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(Now(app));
        var user = await app.Data.UserAsync(remainingVisits: 10, validUntil: today);
        var classId = await app.Data.ClassAsync(startsAt: Now(app).AddDays(30));
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(9, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(3, 4)]
    [DataRow(0, 1)]
    [DataRow(1, int.MaxValue)]
    public async Task Enroll_MoreSeatsThanVisits_Returns409PassExhaustedWithoutTrace(int visits, int seats)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: visits);
        var classId = await app.Data.ClassAsync(capacity: int.MaxValue);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, seats);

        await AssertConflictAsync(response, "pass-exhausted");
        await AssertNoTraceAsync(app, user, visits);
        await AssertNoEnrollmentsAsync(app, classId);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(int.MaxValue)]
    public async Task Enroll_SeatsEqualToVisits_Returns201AndLeavesZero(int visits)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: visits);
        var classId = await app.Data.ClassAsync(capacity: int.MaxValue);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, visits);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(visits, body.RootElement.GetProperty("seats").GetInt32());
        Assert.AreEqual(0, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(5, 3, 3)]
    [DataRow(2, 0, 3)]
    [DataRow(2, 3, 1)]
    [DataRow(1, 1, 1)]
    public async Task Enroll_NotEnoughFreeSeats_Returns409CapacityExceededWithoutTrace(int capacity, int taken, int seats)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: capacity);
        if (taken > 0)
        {
            await app.Data.EnrollmentAsync(other.Id, classId, taken);
        }

        var before = await EnrollmentsSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, seats);

        await AssertConflictAsync(response, "capacity-exceeded");
        await AssertNoTraceAsync(app, user, 10);
        CollectionAssert.AreEqual(before, await EnrollmentsSnapshotAsync(app, classId));
    }

    [TestMethod]
    public async Task Enroll_ExactlyFillsClass_Returns201()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 5);
        await app.Data.EnrollmentAsync(other.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, 2);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        await using var context = app.CreateContext();
        Assert.AreEqual(5, await context.Enrollments.Where(x => x.ClassId == classId && x.Status == "active").SumAsync(x => x.Seats));
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_CancelledEnrollmentsOnClass_DoNotTakeSeats()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 3);
        await app.Data.EnrollmentAsync(other.Id, classId, 3, cancelled: true);
        await app.Data.EnrollmentAsync(other.Id, classId, 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, 2);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(8, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_MaxSeatsOnTakenMaxCapacityClass_Returns409CapacityExceededWithoutOverflow()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: int.MaxValue);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: int.MaxValue);
        await app.Data.EnrollmentAsync(other.Id, classId, 1);
        var before = await EnrollmentsSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, int.MaxValue);

        await AssertConflictAsync(response, "capacity-exceeded");
        await AssertNoTraceAsync(app, user, int.MaxValue);
        CollectionAssert.AreEqual(before, await EnrollmentsSnapshotAsync(app, classId));
    }

    [TestMethod]
    public async Task Enroll_ActiveSeatsSumAboveIntMax_Returns409CapacityExceeded()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: int.MaxValue);
        await app.Data.EnrollmentAsync(other.Id, classId, int.MaxValue);
        await app.Data.EnrollmentAsync(other.Id, classId, int.MaxValue);
        var before = await EnrollmentsSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, 1);

        await AssertConflictAsync(response, "capacity-exceeded");
        await AssertNoTraceAsync(app, user, 10);
        CollectionAssert.AreEqual(before, await EnrollmentsSnapshotAsync(app, classId));
    }

    [TestMethod]
    [DataRow(true, true, false, false, false, "class-cancelled")]
    [DataRow(true, false, true, false, false, "class-cancelled")]
    [DataRow(true, false, false, true, false, "class-cancelled")]
    [DataRow(true, false, false, false, true, "class-cancelled")]
    [DataRow(false, true, true, false, false, "class-started")]
    [DataRow(false, true, false, true, false, "class-started")]
    [DataRow(false, true, false, false, true, "class-started")]
    [DataRow(false, false, true, true, false, "pass-expired")]
    [DataRow(false, false, true, false, true, "pass-expired")]
    [DataRow(false, false, false, true, true, "pass-exhausted")]
    [DataRow(true, true, true, true, true, "class-cancelled")]
    public async Task Enroll_SeveralReasons_Returns409WithFirstByPriority(
        bool cancelled, bool started, bool expired, bool exhausted, bool full, string reason)
    {
        await using var app = await TestApp.StartAsync();
        var now = Now(app);
        var today = DateOnly.FromDateTime(now);
        var visits = exhausted ? 1 : 10;
        var user = await app.Data.UserAsync(
            remainingVisits: visits,
            validUntil: expired ? today.AddDays(-1) : today.AddDays(30));
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(
            capacity: full ? 2 : 10,
            startsAt: started ? now.AddHours(-1) : now.AddDays(1),
            cancelled: cancelled);
        await app.Data.EnrollmentAsync(other.Id, classId, 1);
        var before = await EnrollmentsSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, 2);

        await AssertConflictAsync(response, reason);
        await AssertNoTraceAsync(app, user, visits);
        CollectionAssert.AreEqual(before, await EnrollmentsSnapshotAsync(app, classId));
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }
}
