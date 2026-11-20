using System.Net;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Enrollments;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.ChangeSeatsApi;
using static WebApi.Tests.Enrollments.EnrollApi;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.CardCache;

[TestClass]
public class PassVersionTests
{
    [TestMethod]
    public async Task Enroll_Created_BumpsAuthorVersionByOneEachTime()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 5);
        var bystander = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var first = await EnrollAsync(client, classId, 2);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
        Assert.AreEqual(1L, await VersionAsync(app, user));

        using var second = await EnrollAsync(client, classId, 1);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode, await second.Content.ReadAsStringAsync());
        Assert.AreEqual(2L, await VersionAsync(app, user));
        Assert.AreEqual(0L, await VersionAsync(app, bystander));
    }

    [TestMethod]
    public async Task Enroll_Replay_DoesNotBump()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";
        using var created = await EnrollApi.PostAsync(client, json, "key-1");
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

        using var replay = await EnrollApi.PostAsync(client, json, "key-1");

        Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode);
        Assert.AreEqual(1L, await VersionAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_KeyReused_DoesNotBump()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        using var created = await EnrollApi.PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", "key-1");
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

        using var reused = await EnrollApi.PostAsync(client, $"{{\"classId\":{classId},\"seats\":2}}", "key-1");

        await AssertConflictAsync(reused, "idempotency-key-reused");
        Assert.AreEqual(1L, await VersionAsync(app, user));
    }

    [TestMethod]
    [DataRow("class-cancelled")]
    [DataRow("class-started")]
    [DataRow("pass-expired")]
    [DataRow("pass-exhausted")]
    [DataRow("capacity-exceeded")]
    public async Task Enroll_Rejected_DoesNotBump(string reason)
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(Now(app));
        var user = await app.Data.UserAsync(
            remainingVisits: reason == "pass-exhausted" ? 1 : 10,
            validUntil: reason == "pass-expired" ? today.AddDays(-1) : null);
        var classId = await app.Data.ClassAsync(
            capacity: reason == "capacity-exceeded" ? 1 : 10,
            startsAt: reason == "class-started" ? Now(app) : null,
            cancelled: reason == "class-cancelled");
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, 2);

        await AssertConflictAsync(response, reason);
        Assert.AreEqual(0L, await VersionAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_ClassNotFound_DoesNotBump()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, 999999999, 1);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(0L, await VersionAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_LockTimeout_Returns503AndDoesNotBump()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HoldPassAsync(app, user);

        using var response = await EnrollAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await held.DisposeAsync();
        Assert.AreEqual(0L, await VersionAsync(app, user));
    }

    [TestMethod]
    [DataRow(3, 4)]
    [DataRow(3, 2)]
    public async Task ChangeSeats_Changed_BumpsAuthorVersionOnce(int seats, int newSeats)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var bystander = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, seats);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, newSeats);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(1L, await VersionAsync(app, user));
        Assert.AreEqual(0L, await VersionAsync(app, bystander));
    }

    [TestMethod]
    public async Task ChangeSeats_SameSeats_DoesNotBump()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 3);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(0L, await VersionAsync(app, user));
    }

    [TestMethod]
    [DataRow("enrollment-cancelled", 3)]
    [DataRow("class-cancelled", 3)]
    [DataRow("class-started", 1)]
    [DataRow("pass-expired", 3)]
    [DataRow("pass-exhausted", 3)]
    [DataRow("capacity-exceeded", 3)]
    public async Task ChangeSeats_Rejected_DoesNotBump(string reason, int newSeats)
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(Now(app));
        var user = await app.Data.UserAsync(
            remainingVisits: reason == "pass-exhausted" ? 0 : 10,
            validUntil: reason == "pass-expired" ? today.AddDays(-1) : null);
        var classId = await app.Data.ClassAsync(
            capacity: reason == "capacity-exceeded" ? 2 : 10,
            startsAt: reason == "class-started" ? Now(app) : null,
            cancelled: reason == "class-cancelled");
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2, cancelled: reason == "enrollment-cancelled");
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, newSeats);

        await AssertConflictAsync(response, reason);
        Assert.AreEqual(0L, await VersionAsync(app, user));
    }

    [TestMethod]
    public async Task ChangeSeats_LockTimeout_Returns503AndDoesNotBump()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HoldPassAsync(app, user);

        using var response = await ChangeSeatsAsync(client, id, 3);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await held.DisposeAsync();
        Assert.AreEqual(0L, await VersionAsync(app, user));
    }

    [TestMethod]
    [DataRow(3, false, 3)]
    [DataRow(1, false, 0)]
    [DataRow(1, true, 3)]
    public async Task Cancel_Cancelled_BumpsAuthorVersionOnce(int hoursBefore, bool classCancelled, int refunded)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var bystander = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(startsAt: Now(app).AddHours(hoursBefore), cancelled: classCancelled);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, refunded);
        Assert.AreEqual(4 + refunded, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(1L, await VersionAsync(app, user));
        Assert.AreEqual(0L, await VersionAsync(app, bystander));
    }

    [TestMethod]
    public async Task Cancel_Repeated_DoesNotBumpAgain()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);
        using var first = await CancelAsync(client, id);
        await AssertCancelledAsync(first, id, 2);

        using var repeat = await CancelAsync(client, id);

        await AssertCancelledAsync(repeat, id, 2);
        Assert.AreEqual(1L, await VersionAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_ClassStarted_DoesNotBump()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(startsAt: Now(app));
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        await AssertConflictAsync(response, "class-started");
        Assert.AreEqual(0L, await VersionAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_LockTimeout_Returns503AndDoesNotBump()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HoldPassAsync(app, user);

        using var response = await CancelAsync(client, id);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await held.DisposeAsync();
        Assert.AreEqual(0L, await VersionAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_RefundOverflowsAfterWrite_RollsBackVersion()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: int.MaxValue);
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, id);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual(int.MaxValue, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(0L, await VersionAsync(app, user));
    }

    [TestMethod]
    public async Task Cancel_Promotion_BumpsEachPromotedAndSkippedKeepTheirs()
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(Now(app));
        var owner = await app.Data.UserAsync();
        var exhausted = await app.Data.UserAsync(remainingVisits: 0);
        var expired = await app.Data.UserAsync(validUntil: today.AddDays(-1));
        var waiter = await app.Data.UserAsync();
        var other = await app.Data.UserAsync();
        var late = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 4);
        var id = await app.Data.EnrollmentAsync(owner.Id, classId, 4);
        await EntryAsync(app, owner.Id, classId, 1);
        await EntryAsync(app, exhausted.Id, classId, 1);
        await EntryAsync(app, expired.Id, classId, 1);
        await EntryAsync(app, waiter.Id, classId, 3);
        await EntryAsync(app, other.Id, classId, 1);
        await EntryAsync(app, late.Id, classId, 1);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 4);
        Assert.AreEqual(7, await RemainingVisitsAsync(app, waiter));
        Assert.AreEqual(9, await RemainingVisitsAsync(app, other));
        Assert.AreEqual(10, await RemainingVisitsAsync(app, late));
        CollectionAssert.AreEqual(
            new[] { 1L, 0L, 0L, 1L, 1L, 0L },
            new[]
            {
                await VersionAsync(app, owner),
                await VersionAsync(app, exhausted),
                await VersionAsync(app, expired),
                await VersionAsync(app, waiter),
                await VersionAsync(app, other),
                await VersionAsync(app, late)
            });
    }

    [TestMethod]
    public async Task ChangeSeats_DecreasePromotes_BumpsPromoted()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var waiter = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 3);
        var id = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        await EntryAsync(app, waiter.Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await ChangeSeatsAsync(client, id, 1);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(8, await RemainingVisitsAsync(app, waiter));
        Assert.AreEqual(1L, await VersionAsync(app, owner));
        Assert.AreEqual(1L, await VersionAsync(app, waiter));
    }

    [TestMethod]
    public async Task Waitlist_JoinAndLeave_DoNotBump()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);

        using var join = await JoinAsync(client, classId, 1);
        Assert.AreEqual(HttpStatusCode.Created, join.StatusCode, await join.Content.ReadAsStringAsync());
        Assert.AreEqual(0L, await VersionAsync(app, user));
        var entryId = await EntryIdAsync(app, user);

        using var leave = await client.PostAsync($"/enrollments/waitlist/{entryId}/cancel", null);

        Assert.AreEqual(HttpStatusCode.OK, leave.StatusCode, await leave.Content.ReadAsStringAsync());
        Assert.AreEqual(0L, await VersionAsync(app, user));
    }

    private static async Task<long> VersionAsync(TestApp app, TestUser user)
    {
        await using var context = app.CreateContext();

        return await context.Passes.Where(x => x.UserId == user.Id).Select(x => x.Version).SingleAsync();
    }

    private static async Task<long> EntryIdAsync(TestApp app, TestUser user)
    {
        await using var context = app.CreateContext();

        return await context.WaitlistEntries.Where(x => x.UserId == user.Id).Select(x => x.Id).SingleAsync();
    }

    private static Task<HeldLock> HoldPassAsync(TestApp app, TestUser user)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM passes WHERE user_id = {user.Id} FOR UPDATE");
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }
}
