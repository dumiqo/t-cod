using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Contract;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.EnrollApi;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class LeaveWaitlistTests
{
    [TestMethod]
    public async Task Leave_Waiting_Returns200AndCancelsEntry()
    {
        await using var app = await TestApp.StartAsync();
        var start = Now(app);
        var user = await app.Data.UserAsync(remainingVisits: 3);
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, user.Id, classId, 2);
        app.Clock.Advance(TimeSpan.FromTicks(1234567));
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await LeaveAsync(client, id);

        await AssertLeftAsync(response, id);
        var entry = await ReadEntryAsync(app, id);
        Assert.AreEqual("cancelled", entry.Status);
        Assert.AreEqual(start.AddTicks(1234560), entry.CancelledAt);
        Assert.AreEqual(start, entry.CreatedAt);
        Assert.AreEqual(2, entry.Seats);
        Assert.AreEqual(user.Id, entry.UserId);
        Assert.AreEqual(classId, entry.ClassId);
        Assert.AreEqual(3, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Leave_EntryAhead_NextJoinerGetsPositionOneLessAndOrderKept()
    {
        await using var app = await TestApp.StartAsync();
        var first = await app.Data.UserAsync();
        var second = await app.Data.UserAsync();
        var third = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var firstId = await EntryAsync(app, first.Id, classId, 1);
        var secondId = await EntryAsync(app, second.Id, classId, 1);
        var secondBefore = (await WaitlistSnapshotAsync(app, classId))[1];
        using var firstClient = await app.CreateClientAsync(first.Email);
        using var thirdClient = await app.CreateClientAsync(third.Email);

        using var response = await LeaveAsync(firstClient, firstId);
        using var join = await JoinAsync(thirdClient, classId, 1);

        await AssertLeftAsync(response, firstId);
        var joinText = await join.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, join.StatusCode, joinText);
        Assert.AreEqual(2, JsonNode.Parse(joinText)!["position"]!.GetValue<int>());
        var snapshot = await WaitlistSnapshotAsync(app, classId);
        Assert.AreEqual(3, snapshot.Length);
        Assert.AreEqual(secondBefore, snapshot[1]);
        await using var context = app.CreateContext();
        CollectionAssert.AreEqual(
            new[] { secondId, JsonNode.Parse(joinText)!["id"]!.GetValue<long>() },
            await context.WaitlistEntries.Where(x => x.ClassId == classId && x.Status == "waiting")
                .OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync());
    }

    [TestMethod]
    public async Task Leave_Repeated_ReturnsSameBodyAndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, user.Id, classId, 1);
        using var client = await app.CreateClientAsync(user.Email);
        using var first = await LeaveAsync(client, id);
        var firstText = await first.Content.ReadAsStringAsync();
        var snapshot = await WaitlistSnapshotAsync(app, classId);
        app.Clock.Advance(TimeSpan.FromHours(1));

        using var repeat = await LeaveAsync(client, id);

        await AssertLeftAsync(repeat, id);
        Assert.AreEqual(firstText, await repeat.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(snapshot, await WaitlistSnapshotAsync(app, classId));
    }

    [TestMethod]
    public async Task Leave_AlreadyCancelledEntry_Returns200AndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, user.Id, classId, 2, "cancelled");
        var snapshot = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await LeaveAsync(client, id);

        await AssertLeftAsync(response, id);
        CollectionAssert.AreEqual(snapshot, await WaitlistSnapshotAsync(app, classId));
    }

    [TestMethod]
    public async Task Leave_CancelledClass_Returns200AndCancelsEntry()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(cancelled: true);
        var id = await EntryAsync(app, user.Id, classId, 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await LeaveAsync(client, id);

        await AssertLeftAsync(response, id);
        var entry = await ReadEntryAsync(app, id);
        Assert.AreEqual("cancelled", entry.Status);
        Assert.AreEqual(Now(app), entry.CancelledAt);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    [DataRow(36000000000L)]
    public async Task Leave_ClassStarted_Returns200AndCancelsEntry(long ticksAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var startsAt = Now(app).AddHours(1);
        var classId = await app.Data.ClassAsync(capacity: 1, startsAt: startsAt);
        var id = await EntryAsync(app, user.Id, classId, 2);
        app.Clock.SetUtcNow(new DateTimeOffset(startsAt.AddTicks(ticksAfterStart), TimeSpan.Zero));
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await LeaveAsync(client, id);

        await AssertLeftAsync(response, id);
        var entry = await ReadEntryAsync(app, id);
        Assert.AreEqual("cancelled", entry.Status);
        Assert.AreEqual(startsAt.AddTicks(ticksAfterStart / 10 * 10), entry.CancelledAt);
    }

    [TestMethod]
    public async Task Leave_Promoted_Returns409AndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        var id = await EntryAsync(app, user.Id, classId, 2, "promoted");
        await EnrollmentWithIdAsync(app, id, user.Id, classId, 2, "active");
        var entries = await WaitlistSnapshotAsync(app, classId);
        var enrollment = await SnapshotAsync(app, id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await LeaveAsync(client, id);

        await AssertConflictAsync(response, "already-promoted");
        CollectionAssert.AreEqual(entries, await WaitlistSnapshotAsync(app, classId));
        Assert.AreEqual(enrollment, await SnapshotAsync(app, id));
    }

    [TestMethod]
    public async Task Leave_PromotedThenEnrollmentCancelled_Returns409AndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        var id = await EntryAsync(app, user.Id, classId, 2, "promoted");
        await EnrollmentWithIdAsync(app, id, user.Id, classId, 2, "cancelled");
        var entries = await WaitlistSnapshotAsync(app, classId);
        var enrollment = await SnapshotAsync(app, id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await LeaveAsync(client, id);

        await AssertConflictAsync(response, "already-promoted");
        CollectionAssert.AreEqual(entries, await WaitlistSnapshotAsync(app, classId));
        Assert.AreEqual(enrollment, await SnapshotAsync(app, id));
    }

    [TestMethod]
    public async Task Leave_NoToken_Returns401AndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, user.Id, classId, 1);
        var snapshot = await WaitlistSnapshotAsync(app, classId);
        using var client = app.CreateClient();

        using var response = await LeaveAsync(client, id);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(snapshot, await WaitlistSnapshotAsync(app, classId));
    }

    [TestMethod]
    public async Task Leave_InvalidToken_Returns401AndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, user.Id, classId, 1);
        var snapshot = await WaitlistSnapshotAsync(app, classId);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        using var response = await LeaveAsync(client, id);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        CollectionAssert.AreEqual(snapshot, await WaitlistSnapshotAsync(app, classId));
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("1.5")]
    [DataRow("9223372036854775808")]
    public async Task Leave_IdNotLong_Returns400WithIdError(string id)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        await EntryAsync(app, user.Id, classId, 1);
        var snapshot = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await client.PostAsync($"/enrollments/waitlist/{id}/cancel", null);

        await InputRulesTests.AssertValidationProblemAsync(response, "id");
        CollectionAssert.AreEqual(snapshot, await WaitlistSnapshotAsync(app, classId));
    }

    [TestMethod]
    [DataRow(999999999L)]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public async Task Leave_UnknownId_Returns404(long id)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        await EntryAsync(app, user.Id, classId, 1);
        var snapshot = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await LeaveAsync(client, id);

        await AssertNotFoundAsync(response);
        CollectionAssert.AreEqual(snapshot, await WaitlistSnapshotAsync(app, classId));
    }

    [TestMethod]
    [DataRow("waiting")]
    [DataRow("cancelled")]
    [DataRow("promoted")]
    public async Task Leave_OtherUsersEntry_Returns404AndChangesNothing(string status)
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, owner.Id, classId, 1, status);
        var snapshot = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await LeaveAsync(client, id);

        await AssertNotFoundAsync(response);
        CollectionAssert.AreEqual(snapshot, await WaitlistSnapshotAsync(app, classId));
    }

    [TestMethod]
    public async Task Leave_AdminOnOtherUsersEntry_Returns404AndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, owner.Id, classId, 1);
        var snapshot = await WaitlistSnapshotAsync(app, classId);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await LeaveAsync(client, id);

        await AssertNotFoundAsync(response);
        CollectionAssert.AreEqual(snapshot, await WaitlistSnapshotAsync(app, classId));
    }

    [TestMethod]
    public async Task Leave_EnrollmentId_Returns404AndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        var enrollment = await SnapshotAsync(app, id);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await LeaveAsync(client, id);

        await AssertNotFoundAsync(response);
        Assert.AreEqual(enrollment, await SnapshotAsync(app, id));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Leave_CancelledWhileWaitingForClassLock_Returns200AndKeepsFirstCancelledAt()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, user.Id, classId, 1);
        var firstLeft = Now(app).AddMinutes(-5);
        app.Clock.Advance(TimeSpan.FromMinutes(10));
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldClassAsync(app, classId);
        var request = LeaveAsync(client, id);
        await held.WaitForBlockedAsync(1);
        await held.ExecuteAsync(
            $"UPDATE waitlist_entries SET status = 'cancelled', cancelled_at = '{firstLeft:O}' WHERE id = {id}");
        await held.CommitAsync();
        using var response = await request;

        await AssertLeftAsync(response, id);
        var entry = await ReadEntryAsync(app, id);
        Assert.AreEqual("cancelled", entry.Status);
        Assert.AreEqual(firstLeft, entry.CancelledAt);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Leave_PromotedWhileWaitingForClassLock_Returns409AndChangesNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        var id = await EntryAsync(app, user.Id, classId, 2);
        var now = Now(app);
        using var client = await app.CreateClientAsync(user.Email);

        await using var held = await HoldClassAsync(app, classId);
        var request = LeaveAsync(client, id);
        await held.WaitForBlockedAsync(1);
        await held.ExecuteAsync($"UPDATE waitlist_entries SET status = 'promoted' WHERE id = {id}");
        await held.ExecuteAsync(
            "INSERT INTO enrollments (id, user_id, class_id, seats, status, created_at) " +
            $"VALUES ({id}, {user.Id}, {classId}, 2, 'active', '{now:O}')");
        await held.CommitAsync();
        var entries = await WaitlistSnapshotAsync(app, classId);
        using var response = await request;

        await AssertConflictAsync(response, "already-promoted");
        CollectionAssert.AreEqual(entries, await WaitlistSnapshotAsync(app, classId));
        Assert.AreEqual("promoted", (await ReadEntryAsync(app, id)).Status);
        Assert.AreEqual($"{id}|{user.Id}|{classId}|2|active|{now:O}||", await SnapshotAsync(app, id));
    }

    private static Task<HttpResponseMessage> LeaveAsync(HttpClient client, long id)
    {
        return client.PostAsync($"/enrollments/waitlist/{id}/cancel", null);
    }

    private static async Task AssertLeftAsync(HttpResponseMessage response, long id)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        var expected = JsonSerializer.Serialize(new { id, status = "cancelled" });
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(text)), $"expected {expected}, got {text}");
    }

    private static async Task<WaitlistEntry> ReadEntryAsync(TestApp app, long id)
    {
        await using var context = app.CreateContext();

        return await context.WaitlistEntries.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static async Task EnrollmentWithIdAsync(TestApp app, long id, long userId, long classId, int seats, string status)
    {
        var now = Now(app);
        await using var context = app.CreateContext();
        context.Enrollments.Add(new Enrollment
        {
            Id = id,
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            Status = status,
            CreatedAt = now,
            CancelledAt = status == "cancelled" ? now : null,
            RefundedVisits = status == "cancelled" ? seats : null
        });
        await context.SaveChangesAsync();
    }

    private static Task<HeldLock> HoldClassAsync(TestApp app, long classId)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");
    }

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }
}
