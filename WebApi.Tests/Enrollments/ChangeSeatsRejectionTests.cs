using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.ChangeSeatsApi;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class ChangeSeatsRejectionTests
{
    [TestMethod]
    [DataRow(true, false, false, false, false, false, "enrollment-cancelled")]
    [DataRow(false, true, false, false, false, false, "class-cancelled")]
    [DataRow(false, false, true, false, false, false, "class-started")]
    [DataRow(false, false, false, true, false, false, "pass-expired")]
    [DataRow(false, false, false, false, true, false, "pass-exhausted")]
    [DataRow(false, false, false, false, false, true, "capacity-exceeded")]
    [DataRow(true, true, false, false, false, false, "enrollment-cancelled")]
    [DataRow(true, false, true, false, false, false, "enrollment-cancelled")]
    [DataRow(true, false, false, true, false, false, "enrollment-cancelled")]
    [DataRow(true, false, false, false, true, false, "enrollment-cancelled")]
    [DataRow(true, false, false, false, false, true, "enrollment-cancelled")]
    [DataRow(false, true, true, false, false, false, "class-cancelled")]
    [DataRow(false, true, false, true, false, false, "class-cancelled")]
    [DataRow(false, true, false, false, true, false, "class-cancelled")]
    [DataRow(false, true, false, false, false, true, "class-cancelled")]
    [DataRow(false, false, true, true, false, false, "class-started")]
    [DataRow(false, false, true, false, true, false, "class-started")]
    [DataRow(false, false, true, false, false, true, "class-started")]
    [DataRow(false, false, false, true, true, false, "pass-expired")]
    [DataRow(false, false, false, true, false, true, "pass-expired")]
    [DataRow(false, false, false, false, true, true, "pass-exhausted")]
    [DataRow(true, true, true, true, true, true, "enrollment-cancelled")]
    public async Task ChangeSeats_IncreaseWithReasons_Returns409WithFirstByPriorityWithoutTrace(
        bool enrollmentCancelled, bool classCancelled, bool started, bool expired, bool exhausted, bool full, string reason)
    {
        await RejectAsync(4, enrollmentCancelled, classCancelled, started, expired, exhausted, full, reason);
    }

    [TestMethod]
    [DataRow(1, true, false, false, "enrollment-cancelled")]
    [DataRow(1, false, true, false, "class-cancelled")]
    [DataRow(1, false, false, true, "class-started")]
    [DataRow(1, true, true, false, "enrollment-cancelled")]
    [DataRow(1, true, false, true, "enrollment-cancelled")]
    [DataRow(1, false, true, true, "class-cancelled")]
    [DataRow(1, true, true, true, "enrollment-cancelled")]
    [DataRow(2, true, false, false, "enrollment-cancelled")]
    [DataRow(2, false, true, false, "class-cancelled")]
    [DataRow(2, false, false, true, "class-started")]
    [DataRow(2, true, true, false, "enrollment-cancelled")]
    [DataRow(2, true, false, true, "enrollment-cancelled")]
    [DataRow(2, false, true, true, "class-cancelled")]
    [DataRow(2, true, true, true, "enrollment-cancelled")]
    public async Task ChangeSeats_DecreaseOrSameWithReasons_Returns409WithFirstByPriorityWithoutTrace(
        int seats, bool enrollmentCancelled, bool classCancelled, bool started, string reason)
    {
        await RejectAsync(seats, enrollmentCancelled, classCancelled, started, false, false, false, reason);
    }

    [TestMethod]
    [DataRow(-1, 3, DisplayName = "pass expired yesterday")]
    [DataRow(30, 0, DisplayName = "no visits left")]
    public async Task ChangeSeats_SameSeatsWithUnusablePass_Returns200AndChangesNothing(int validDays, int remainingVisits)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits, Today(app).AddDays(validDays));
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 3);
        var snapshot = await SnapshotAsync(app, id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 3);

        var body = await AssertOkAsync(response);
        Assert.AreEqual(3, body["seats"]!.GetValue<int>());
        Assert.AreEqual(remainingVisits, body["pass"]!["remainingVisits"]!.GetValue<int>());
        Assert.AreEqual(snapshot, await SnapshotAsync(app, id));
        Assert.AreEqual(remainingVisits, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task ChangeSeats_IncreaseExactlyToCapacity_Returns200()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 5);
        await app.Data.EnrollmentAsync(other.Id, classId, 2);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 3);

        var body = await AssertOkAsync(response);
        Assert.AreEqual(3, body["seats"]!.GetValue<int>());
        await AssertNoChangeTraceAsync(app, user, id, 3, 9);
        Assert.AreEqual(5, await ActiveSeatsAsync(app, classId));
    }

    [TestMethod]
    public async Task ChangeSeats_IncreaseOneOverCapacity_Returns409CapacityExceededWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 5);
        await app.Data.EnrollmentAsync(other.Id, classId, 2);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        var before = await EnrollmentsSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 4);

        await AssertConflictAsync(response, "capacity-exceeded");
        await AssertNoChangeTraceAsync(app, user, id, 2, 10);
        CollectionAssert.AreEqual(before, await EnrollmentsSnapshotAsync(app, classId));
    }

    [TestMethod]
    public async Task ChangeSeats_IncreaseByExactlyRemainingVisits_Returns200AndLeavesZero()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 2);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 3);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 5);

        var body = await AssertOkAsync(response);
        Assert.AreEqual(0, body["pass"]!["remainingVisits"]!.GetValue<int>());
        await AssertNoChangeTraceAsync(app, user, id, 5, 0);
    }

    [TestMethod]
    [DataRow(1, 3, 5)]
    [DataRow(0, 3, 4)]
    [DataRow(1, 1, int.MaxValue)]
    public async Task ChangeSeats_IncreaseByMoreThanRemainingVisits_Returns409PassExhaustedWithoutTrace(int visits, int seats, int newSeats)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: visits);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(capacity: int.MaxValue), seats);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, newSeats);

        await AssertConflictAsync(response, "pass-exhausted");
        await AssertNoChangeTraceAsync(app, user, id, seats, visits);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task ChangeSeats_IncreaseAtOrAfterStartsAt_Returns409ClassStartedWithoutTrace(long ticksAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(1);
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(startsAt: startsAt), 2);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(ticksAfterStart));

        using var response = await ChangeSeatsAsync(client, id, 3);

        await AssertConflictAsync(response, "class-started");
        await AssertNoChangeTraceAsync(app, user, id, 2, 10);
    }

    [TestMethod]
    public async Task ChangeSeats_IncreaseOneTickBeforeStartsAt_Returns200()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(1);
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(startsAt: startsAt), 2);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(-1));

        using var response = await ChangeSeatsAsync(client, id, 3);

        await AssertOkAsync(response);
        await AssertNoChangeTraceAsync(app, user, id, 3, 9);
    }

    [TestMethod]
    public async Task ChangeSeats_IncreaseAtLastTickOfValidUntil_Returns200()
    {
        await using var app = await TestApp.StartAsync();
        var today = Today(app);
        var user = await app.Data.UserAsync(remainingVisits: 10, validUntil: today);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(startsAt: Now(app).AddDays(10)), 2);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(today.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));

        using var response = await ChangeSeatsAsync(client, id, 3);

        await AssertOkAsync(response);
        await AssertNoChangeTraceAsync(app, user, id, 3, 9);
    }

    [TestMethod]
    public async Task ChangeSeats_IncreaseOnDayAfterValidUntil_Returns409PassExpiredWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var today = Today(app);
        var user = await app.Data.UserAsync(remainingVisits: 10, validUntil: today);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(startsAt: Now(app).AddDays(10)), 2);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        using var response = await ChangeSeatsAsync(client, id, 3);

        await AssertConflictAsync(response, "pass-expired");
        await AssertNoChangeTraceAsync(app, user, id, 2, 10);
    }

    [TestMethod]
    [DataRow(-2 * TimeSpan.TicksPerHour, DisplayName = "exactly two hours before")]
    [DataRow(-2 * TimeSpan.TicksPerHour + 1, DisplayName = "one tick inside two hours")]
    [DataRow(-1L, DisplayName = "one tick before start")]
    public async Task ChangeSeats_DecreaseCloseToStart_RefundsWholeDifferenceAndFreesSeats(long ticksFromStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(3);
        var user = await app.Data.UserAsync(remainingVisits: 1);
        var other = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(capacity: 5, startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 5);
        using var client = await app.CreateClientAsync(user.Email);
        using var otherClient = await app.CreateClientAsync(other.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(ticksFromStart));

        using var response = await ChangeSeatsAsync(client, id, 2);

        var body = await AssertOkAsync(response);
        Assert.AreEqual(2, body["seats"]!.GetValue<int>());
        Assert.AreEqual(4, body["pass"]!["remainingVisits"]!.GetValue<int>());
        await AssertNoChangeTraceAsync(app, user, id, 2, 4);
        using var tooMany = await EnrollAsync(otherClient, classId, 4);
        using var exact = await EnrollAsync(otherClient, classId, 3);
        await AssertConflictAsync(tooMany, "capacity-exceeded");
        Assert.AreEqual(HttpStatusCode.Created, exact.StatusCode, await exact.Content.ReadAsStringAsync());
        Assert.AreEqual(5, await ActiveSeatsAsync(app, classId));
    }

    [TestMethod]
    public async Task ChangeSeats_DecreaseThenCancelWithinTwoHours_RefundsDifferenceOnlyOnce()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 0);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(startsAt: Now(app).AddHours(1)), 5);
        using var client = await app.CreateClientAsync(user.Email);

        using var change = await ChangeSeatsAsync(client, id, 1);

        var body = await AssertOkAsync(change);
        Assert.AreEqual(4, body["pass"]!["remainingVisits"]!.GetValue<int>());
        await AssertNoChangeTraceAsync(app, user, id, 1, 4);

        using var cancel = await CancelAsync(client, id);

        await AssertCancelledAsync(cancel, id, 0);
        Assert.AreEqual(4, await RemainingVisitsAsync(app, user));
        var enrollment = await EnrollmentAsync(app, id);
        Assert.AreEqual("cancelled", enrollment.Status);
        Assert.AreEqual(0, enrollment.RefundedVisits);
        Assert.AreEqual(1, enrollment.Seats);
        using var card = await client.GetAsync($"/enrollments/{id}");
        var cardBody = JsonNode.Parse(await card.Content.ReadAsStringAsync())!;
        Assert.AreEqual(1, cardBody["seats"]!.GetValue<int>());
        Assert.AreEqual("cancelled", cardBody["status"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ChangeSeats_IncreaseToIntMaxOnClassWithIntMaxEnrollments_Returns409CapacityExceeded()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: int.MaxValue);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: int.MaxValue);
        await app.Data.EnrollmentAsync(other.Id, classId, int.MaxValue);
        await app.Data.EnrollmentAsync(other.Id, classId, int.MaxValue);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 1);
        var before = await EnrollmentsSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, int.MaxValue);

        await AssertConflictAsync(response, "capacity-exceeded");
        await AssertNoChangeTraceAsync(app, user, id, 1, int.MaxValue);
        CollectionAssert.AreEqual(before, await EnrollmentsSnapshotAsync(app, classId));
    }

    [TestMethod]
    [DataRow(1, 6)]
    [DataRow(2, 5)]
    public async Task ChangeSeats_DecreaseOrSameOnOverfilledClass_Returns200(int newSeats, int remainingVisits)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 2);
        await app.Data.EnrollmentAsync(other.Id, classId, 3);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, newSeats);

        var body = await AssertOkAsync(response);
        Assert.AreEqual(newSeats, body["seats"]!.GetValue<int>());
        await AssertNoChangeTraceAsync(app, user, id, newSeats, remainingVisits);
    }

    [TestMethod]
    public async Task ChangeSeats_IncreaseOnOverfilledClass_Returns409CapacityExceededWithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 2);
        await app.Data.EnrollmentAsync(other.Id, classId, 3);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 3);

        await AssertConflictAsync(response, "capacity-exceeded");
        await AssertNoChangeTraceAsync(app, user, id, 2, 5);
    }

    [TestMethod]
    public async Task ChangeSeats_DecreaseOverflowsRemainingVisits_Returns500WithoutTrace()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: int.MaxValue);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 1);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode, await response.Content.ReadAsStringAsync());
        await AssertNoChangeTraceAsync(app, user, id, 2, int.MaxValue);
    }

    [TestMethod]
    [DataRow(true, false, true, "enrollment-cancelled", DisplayName = "cancelled on started class")]
    [DataRow(true, true, false, "enrollment-cancelled", DisplayName = "cancelled on cancelled class")]
    [DataRow(false, true, true, "class-cancelled", DisplayName = "active on cancelled past class")]
    public async Task ChangeSeats_SeededEnrollment_Returns409AndChangesNothing(
        bool enrollmentCancelled, bool classCancelled, bool started, string reason)
    {
        await using var app = await TestApp.StartAsync();
        var now = Now(app);
        var status = enrollmentCancelled ? "cancelled" : "active";
        TestUser user;
        long id;
        int seats;
        long classId;
        await using (var context = app.CreateContext())
        {
            var found = await context.Enrollments.AsNoTracking()
                .Join(context.Classes.IgnoreQueryFilters(), e => e.ClassId, c => c.Id, (e, c) => new { e, c })
                .Where(x => x.e.Status == status && x.c.IsCancelled == classCancelled && (!started || x.c.StartsAt <= now))
                .OrderBy(x => x.e.Id)
                .Select(x => new { x.e.Id, x.e.UserId, x.e.Seats, x.e.ClassId })
                .FirstAsync();
            var email = await context.Users.Where(x => x.Id == found.UserId).Select(x => x.Email).SingleAsync();
            user = new TestUser(found.UserId, email);
            id = found.Id;
            seats = found.Seats;
            classId = found.ClassId;
        }

        var remaining = await RemainingVisitsAsync(app, user);
        var snapshot = await SnapshotAsync(app, id);
        var before = await EnrollmentsSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, seats + 1);

        await AssertConflictAsync(response, reason);
        Assert.AreEqual(snapshot, await SnapshotAsync(app, id));
        CollectionAssert.AreEqual(before, await EnrollmentsSnapshotAsync(app, classId));
        Assert.AreEqual(remaining, await RemainingVisitsAsync(app, user));
    }

    private static async Task RejectAsync(
        int newSeats, bool enrollmentCancelled, bool classCancelled, bool started, bool expired, bool exhausted, bool full, string reason)
    {
        await using var app = await TestApp.StartAsync();
        var now = Now(app);
        var visits = exhausted ? 1 : 10;
        var user = await app.Data.UserAsync(visits, expired ? Today(app).AddDays(-1) : Today(app).AddDays(30));
        var other = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(
            capacity: full ? 4 : 10,
            startsAt: started ? now.AddHours(-1) : now.AddDays(1),
            cancelled: classCancelled);
        await app.Data.EnrollmentAsync(other.Id, classId, 1);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2, cancelled: enrollmentCancelled);
        var snapshot = await SnapshotAsync(app, id);
        var before = await EnrollmentsSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, newSeats);

        await AssertConflictAsync(response, reason);
        Assert.AreEqual(snapshot, await SnapshotAsync(app, id));
        CollectionAssert.AreEqual(before, await EnrollmentsSnapshotAsync(app, classId));
        Assert.AreEqual(visits, await RemainingVisitsAsync(app, user));
    }

    private static async Task<JsonNode> AssertOkAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);

        return JsonNode.Parse(text)!;
    }

    private static async Task<long> ActiveSeatsAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();

        return await context.Enrollments
            .Where(x => x.ClassId == classId && x.Status == "active")
            .SumAsync(x => (long)x.Seats);
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }

    private static DateOnly Today(TestApp app)
    {
        return DateOnly.FromDateTime(Now(app));
    }
}
