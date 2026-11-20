using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.ChangeSeatsApi;
using static WebApi.Tests.Enrollments.EnrollApi;
using static WebApi.Tests.Waitlist.PromotionOnCancelTests;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class PromotionOnSeatsDecreaseTests
{
    [TestMethod]
    public async Task ChangeSeats_DecreaseFreesSeatsForWaiter_PromotesAndReturnsOwnCard()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 3);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        var entry = await EntryAsync(app, waiter.Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await ChangeSeatsAsync(client, ownerEnrollment, 1);

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        var root = JsonDocument.Parse(text).RootElement;
        Assert.AreEqual(ownerEnrollment, root.GetProperty("id").GetInt64());
        Assert.AreEqual(1, root.GetProperty("seats").GetInt32());
        Assert.AreEqual(7, root.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        CollectionAssert.AreEqual(new[] { "promoted|2" }, await QueueAsync(app, entry));
        await AssertEnrollmentStateAsync(app, owner, ownerEnrollment, 1, 7);
        Assert.AreEqual(3, await RemainingVisitsAsync(app, waiter));
        Assert.AreEqual(3, await ActiveSeatsAsync(app, classId));
    }

    [TestMethod]
    public async Task ChangeSeats_DecreaseWithIdleSeat_HandsOutAllFreeSeatsSkippingWhatDoesNotFit()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var filler = await app.Data.UserAsync();
        var users = new[] { await app.Data.UserAsync(), await app.Data.UserAsync(), await app.Data.UserAsync() };
        var classId = await app.Data.ClassAsync(capacity: 5);
        await app.Data.EnrollmentAsync(filler.Id, classId, 1);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        var tooBig = await EntryAsync(app, users[0].Id, classId, 3);
        var fits = await EntryAsync(app, users[1].Id, classId, 2);
        var noLongerFits = await EntryAsync(app, users[2].Id, classId, 1);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await ChangeSeatsAsync(client, ownerEnrollment, 2);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "waiting|-", "promoted|2", "waiting|-" }, await QueueAsync(app, tooBig, fits, noLongerFits));
        Assert.AreEqual(5, await ActiveSeatsAsync(app, classId));
        await AssertEnrollmentStateAsync(app, owner, ownerEnrollment, 2, 6);
    }

    [TestMethod]
    public async Task ChangeSeats_DecreaseFirstWaiterExhausted_SkippedAndNextPromoted()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var users = new[] { await app.Data.UserAsync(remainingVisits: 1), await app.Data.UserAsync(remainingVisits: 5) };
        var classId = await app.Data.ClassAsync(capacity: 3);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        var first = await EntryAsync(app, users[0].Id, classId, 2);
        var second = await EntryAsync(app, users[1].Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await ChangeSeatsAsync(client, ownerEnrollment, 1);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "waiting|-", "promoted|2" }, await QueueAsync(app, first, second));
        Assert.AreEqual(1, await RemainingVisitsAsync(app, users[0]));
        Assert.AreEqual(3, await RemainingVisitsAsync(app, users[1]));
    }

    [TestMethod]
    [DataRow(2, 2)]
    [DataRow(2, 3)]
    public async Task ChangeSeats_SameOrMoreSeatsWithIdleSeatAndWaiter_EntryStaysWaiting(int oldSeats, int newSeats)
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 5);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, oldSeats);
        var entry = await EntryAsync(app, waiter.Id, classId, 1);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await ChangeSeatsAsync(client, ownerEnrollment, newSeats);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "waiting|-" }, await QueueAsync(app, entry));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, waiter));
        await AssertEnrollmentStateAsync(app, owner, ownerEnrollment, newSeats, 5 - (newSeats - oldSeats));
    }

    [TestMethod]
    public async Task ChangeSeats_DecreaseOnClassCancelledByClub_Returns409AndQueueUnchanged()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 3, cancelled: true);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        await EntryAsync(app, waiter.Id, classId, 2);
        var queue = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await ChangeSeatsAsync(client, ownerEnrollment, 1);

        await AssertConflictAsync(response, "class-cancelled");
        CollectionAssert.AreEqual(queue, await WaitlistSnapshotAsync(app, classId));
        Assert.AreEqual(1, (await EnrollmentsSnapshotAsync(app, classId)).Length);
        await AssertEnrollmentStateAsync(app, owner, ownerEnrollment, 3, 5);
        Assert.AreEqual(5, await RemainingVisitsAsync(app, waiter));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task ChangeSeats_DecreaseOnStartedClass_Returns409AndQueueUnchanged(long ticksAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddHours(1);
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 3, startsAt: startsAt);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        await EntryAsync(app, waiter.Id, classId, 2);
        var queue = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(owner.Email);
        app.Clock.SetUtcNow(startsAt.AddTicks(ticksAfterStart));

        using var response = await ChangeSeatsAsync(client, ownerEnrollment, 1);

        await AssertConflictAsync(response, "class-started");
        CollectionAssert.AreEqual(queue, await WaitlistSnapshotAsync(app, classId));
        Assert.AreEqual(1, (await EnrollmentsSnapshotAsync(app, classId)).Length);
        await AssertEnrollmentStateAsync(app, owner, ownerEnrollment, 3, 5);
        Assert.AreEqual(5, await RemainingVisitsAsync(app, waiter));
    }

    [TestMethod]
    public async Task ChangeSeats_DecreaseWithOwnWaitingEntry_OwnEntryStaysWaitingAndNextPromoted()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 3);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        var own = await EntryAsync(app, owner.Id, classId, 2);
        var next = await EntryAsync(app, waiter.Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await ChangeSeatsAsync(client, ownerEnrollment, 1);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "waiting|-", "promoted|2" }, await QueueAsync(app, own, next));
        await AssertEnrollmentStateAsync(app, owner, ownerEnrollment, 1, 7);
        Assert.AreEqual(3, await RemainingVisitsAsync(app, waiter));
    }

    [TestMethod]
    public async Task ChangeSeats_DecreaseWithOnlyOwnWaitingEntry_SeatsStayFree()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 3);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        var own = await EntryAsync(app, owner.Id, classId, 2);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await ChangeSeatsAsync(client, ownerEnrollment, 1);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "waiting|-" }, await QueueAsync(app, own));
        Assert.AreEqual(1, await ActiveSeatsAsync(app, classId));
        await AssertEnrollmentStateAsync(app, owner, ownerEnrollment, 1, 7);
    }

    [TestMethod]
    public async Task ChangeSeats_DecreaseWithWaiterWithoutPass_Returns500AndRollsBack()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var waiter = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 3);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        var entry = await EntryAsync(app, waiter.Id, classId, 2);
        await using (var context = app.CreateContext())
        {
            await context.Passes.Where(x => x.UserId == waiter.Id).ExecuteDeleteAsync();
        }

        var queue = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await ChangeSeatsAsync(client, ownerEnrollment, 1);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(queue, await WaitlistSnapshotAsync(app, classId));
        CollectionAssert.AreEqual(new[] { "waiting|-" }, await QueueAsync(app, entry));
        await AssertEnrollmentStateAsync(app, owner, ownerEnrollment, 3, 5);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public async Task ChangeSeats_DecreaseClassStillFullOrOverfull_NobodyPromoted(int fillerSeats)
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync(remainingVisits: 5);
        var filler = await app.Data.UserAsync();
        var waiter = await app.Data.UserAsync(remainingVisits: 5);
        var classId = await app.Data.ClassAsync(capacity: 3);
        await app.Data.EnrollmentAsync(filler.Id, classId, fillerSeats);
        var ownerEnrollment = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        var entry = await EntryAsync(app, waiter.Id, classId, 1);
        using var client = await app.CreateClientAsync(owner.Email);

        using var response = await ChangeSeatsAsync(client, ownerEnrollment, 1);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "waiting|-" }, await QueueAsync(app, entry));
        Assert.AreEqual(5, await RemainingVisitsAsync(app, waiter));
        await AssertEnrollmentStateAsync(app, owner, ownerEnrollment, 1, 6);
    }

    private static async Task<long> ActiveSeatsAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();

        return await context.Enrollments.Where(x => x.ClassId == classId && x.Status == "active").SumAsync(x => (long)x.Seats);
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }
}
