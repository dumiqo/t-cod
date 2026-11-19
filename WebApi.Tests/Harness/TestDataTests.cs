using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Harness;

[TestClass]
public class TestDataTests
{
    [TestMethod]
    public async Task UserAsync_Defaults_CreatesUserWithValidPass()
    {
        await using var app = await TestApp.StartAsync();
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime);

        var user = await app.Data.UserAsync();

        await using var context = app.CreateContext();
        var stored = await context.Users.SingleOrDefaultAsync(x => x.Id == user.Id);
        var pass = await context.Passes.SingleOrDefaultAsync(x => x.UserId == user.Id);
        Assert.IsNotNull(stored);
        Assert.IsNotNull(pass);
        Assert.AreEqual(user.Email, stored.Email);
        Assert.AreEqual("user", stored.Role);
        Assert.AreEqual(10, pass.RemainingVisits);
        Assert.AreEqual(today.AddDays(30), pass.ValidUntil);
    }

    [TestMethod]
    public async Task UserAsync_GivenPass_StoresVisitsAndValidUntil()
    {
        await using var app = await TestApp.StartAsync();
        var validUntil = new DateOnly(2030, 2, 28);

        var user = await app.Data.UserAsync(remainingVisits: 3, validUntil: validUntil);

        await using var context = app.CreateContext();
        var pass = await context.Passes.SingleOrDefaultAsync(x => x.UserId == user.Id);
        Assert.IsNotNull(pass);
        Assert.AreEqual(3, pass.RemainingVisits);
        Assert.AreEqual(validUntil, pass.ValidUntil);
    }

    [TestMethod]
    public async Task UserAsync_Twice_CreatesDistinctUsers()
    {
        await using var app = await TestApp.StartAsync();

        var first = await app.Data.UserAsync();
        var second = await app.Data.UserAsync();

        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreNotEqual(first.Email, second.Email);
    }

    [TestMethod]
    public async Task UserAsync_CreatedUser_LogsInAsThatUser()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        using var client = app.CreateClient();

        using var login = await client.LoginAsync(user.Email);

        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        using var authorized = await app.CreateClientAsync(user.Email);
        using var response = await authorized.GetAsync("/harness/me");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(user.Id, await response.Content.ReadFromJsonAsync<long>());
    }

    [TestMethod]
    public async Task ClassAsync_Defaults_CreatesClassOneDayAhead()
    {
        await using var app = await TestApp.StartAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;

        var classId = await app.Data.ClassAsync();

        await using var context = app.CreateContext();
        var stored = await context.Classes.SingleOrDefaultAsync(x => x.Id == classId);
        Assert.IsNotNull(stored);
        Assert.AreEqual(10, stored.Capacity);
        Assert.AreEqual(now.AddDays(1), stored.StartsAt);
        Assert.IsFalse(stored.IsCancelled);
        Assert.IsTrue(await context.Rooms.AnyAsync(x => x.Id == stored.RoomId));
        Assert.IsFalse(await context.Enrollments.AnyAsync(x => x.ClassId == classId));
    }

    [TestMethod]
    public async Task ClassAsync_Given_StoresCapacityStartAndCancellation()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var classId = await app.Data.ClassAsync(capacity: 1, startsAt: startsAt, cancelled: true);

        await using var context = app.CreateContext();
        var stored = await context.Classes.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == classId);
        Assert.IsNotNull(stored);
        Assert.AreEqual(1, stored.Capacity);
        Assert.AreEqual(startsAt, stored.StartsAt);
        Assert.IsTrue(stored.IsCancelled);
    }

    [TestMethod]
    public async Task EnrollmentAsync_Defaults_CreatesActiveEnrollmentWithoutChargingVisits()
    {
        await using var app = await TestApp.StartAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync();

        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);

        await using var context = app.CreateContext();
        var stored = await context.Enrollments.SingleOrDefaultAsync(x => x.Id == id);
        Assert.IsNotNull(stored);
        Assert.AreEqual(user.Id, stored.UserId);
        Assert.AreEqual(classId, stored.ClassId);
        Assert.AreEqual(3, stored.Seats);
        Assert.AreEqual("active", stored.Status);
        Assert.AreEqual(now, stored.CreatedAt);
        Assert.IsNull(stored.CancelledAt);
        Assert.IsNull(stored.RefundedVisits);
        Assert.AreEqual(4, await context.Passes.Where(x => x.UserId == user.Id).Select(x => x.RemainingVisits).SingleAsync());
    }

    [TestMethod]
    public async Task EnrollmentAsync_ActiveWithRefundedVisits_StoresNull()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();

        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2, refundedVisits: 2);

        await using var context = app.CreateContext();
        var stored = await context.Enrollments.SingleAsync(x => x.Id == id);
        Assert.AreEqual("active", stored.Status);
        Assert.IsNull(stored.RefundedVisits);
    }

    [TestMethod]
    public async Task EnrollmentAsync_CancelledWithRefundedVisits_StoresGivenValue()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();

        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3, cancelled: true, refundedVisits: 0);

        await using var context = app.CreateContext();
        var stored = await context.Enrollments.SingleAsync(x => x.Id == id);
        Assert.AreEqual("cancelled", stored.Status);
        Assert.AreEqual(0, stored.RefundedVisits);
    }

    [TestMethod]
    public async Task EnrollmentAsync_Cancelled_StoresCancelledAt()
    {
        await using var app = await TestApp.StartAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();

        var id = await app.Data.EnrollmentAsync(user.Id, classId, int.MaxValue, cancelled: true);

        await using var context = app.CreateContext();
        var stored = await context.Enrollments.SingleOrDefaultAsync(x => x.Id == id);
        Assert.IsNotNull(stored);
        Assert.AreEqual(int.MaxValue, stored.Seats);
        Assert.AreEqual("cancelled", stored.Status);
        Assert.AreEqual(now, stored.CancelledAt);
        Assert.AreEqual(int.MaxValue, stored.RefundedVisits);
    }
}
