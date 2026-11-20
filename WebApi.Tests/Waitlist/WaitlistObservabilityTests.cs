using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class WaitlistObservabilityTests
{
    [TestMethod]
    [DataRow(3)]
    [DataRow(int.MaxValue)]
    public async Task Join_Valid_LogsWaitlistJoinedAndCountsSeats(int seats)
    {
        await using var app = await TestApp.StartAsync();
        var other = await app.Data.UserAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        await EntryAsync(app, other.Id, classId, 1);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, seats);

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, text);
        using var body = JsonDocument.Parse(text);
        var record = One(JoinedEvents(app));
        Assert.AreEqual("WaitlistJoined", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual(body.RootElement.GetProperty("id").GetInt64().ToString(), record.GetStructuredStateValue("WaitlistId"));
        Assert.AreEqual(classId.ToString(), record.GetStructuredStateValue("ClassId"));
        Assert.AreEqual(seats.ToString(), record.GetStructuredStateValue("Seats"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        Assert.AreEqual("2", record.GetStructuredStateValue("Position"));
        AssertSingleUntaggedMeasurement(app, "club.waitlist.joined", seats);
        AssertNoRejection(app);
        Assert.AreEqual(0, ReplayEvents(app).Count);
    }

    [TestMethod]
    [DataRow("class-cancelled")]
    [DataRow("class-started")]
    [DataRow("already-waiting")]
    [DataRow("seats-available")]
    public async Task Join_Rejected_LogsRejectionWithoutJoinEventOrCount(string reason)
    {
        await using var app = await TestApp.StartAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var user = await app.Data.UserAsync();
        var filler = await app.Data.UserAsync(remainingVisits: 2);
        var classId = await app.Data.ClassAsync(
            capacity: 2,
            startsAt: reason == "class-started" ? now.AddHours(-1) : now.AddDays(1),
            cancelled: reason == "class-cancelled");
        if (reason != "seats-available")
        {
            await app.Data.EnrollmentAsync(filler.Id, classId, 2);
        }

        if (reason == "already-waiting")
        {
            await EntryAsync(app, user.Id, classId, 1);
        }

        using var client = await app.CreateClientAsync(user.Email);

        using var response = await JoinAsync(client, classId, 1);

        await Enrollments.EnrollApi.AssertConflictAsync(response, reason);
        AssertRejected(app, "join-waitlist", reason, user);
        AssertNoJoin(app);
    }

    [TestMethod]
    public async Task Join_SameKeyOtherBody_LogsRejectionWithOneJoinEvent()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);

        using var first = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", "key-1");
        using var reused = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":2}}", "key-1");

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
        await Enrollments.EnrollApi.AssertConflictAsync(reused, "idempotency-key-reused");
        AssertRejected(app, "join-waitlist", "idempotency-key-reused", user);
        Assert.AreEqual(1, JoinedEvents(app).Count);
        AssertSingleUntaggedMeasurement(app, "club.waitlist.joined", 1);
        Assert.AreEqual(0, ReplayEvents(app).Count);
    }

    [TestMethod]
    public async Task Join_Replay_LogsIdempotentReplayAndCountsWithoutJoinEvent()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":2}}";

        using var first = await PostAsync(client, json, "key-1");
        using var repeat = await PostAsync(client, json, "key-1");

        var text = await repeat.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, text);
        using var body = JsonDocument.Parse(text);
        var record = One(ReplayEvents(app));
        Assert.AreEqual("IdempotentReplay", record.Id.Name);
        Assert.AreEqual("join-waitlist", record.GetStructuredStateValue("Operation"));
        Assert.AreEqual(body.RootElement.GetProperty("id").GetInt64().ToString(), record.GetStructuredStateValue("ResultId"));
        Assert.AreEqual(classId.ToString(), record.GetStructuredStateValue("ClassId"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        var replayed = app.Metrics("club.idempotency.replayed").GetMeasurementSnapshot();
        Assert.AreEqual(1, replayed.Count);
        Assert.AreEqual(1, replayed[0].Value);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, object?> { ["operation"] = "join-waitlist" },
            replayed[0].Tags.ToDictionary(x => x.Key, x => x.Value));
        Assert.AreEqual(1, JoinedEvents(app).Count);
        AssertSingleUntaggedMeasurement(app, "club.waitlist.joined", 2);
        AssertNoRejection(app);
    }

    [TestMethod]
    public async Task Join_ClassLockTimeout_LogsTransactionConflictWithoutJoinEvent()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HoldClassAsync(app, classId);

        using var response = await JoinAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertLockTimeoutObserved(app, "join-waitlist", user);
        AssertNoJoin(app);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(int.MaxValue)]
    public async Task Leave_Waiting_LogsWaitlistLeftOnceAndCountsSeats(int seats)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, user.Id, classId, seats);
        using var client = await app.CreateClientAsync(user.Email);

        using var first = await LeaveAsync(client, id);
        using var repeat = await LeaveAsync(client, id);

        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, repeat.StatusCode, await repeat.Content.ReadAsStringAsync());
        var record = One(LeftEvents(app));
        Assert.AreEqual("WaitlistLeft", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual(id.ToString(), record.GetStructuredStateValue("WaitlistId"));
        Assert.AreEqual(classId.ToString(), record.GetStructuredStateValue("ClassId"));
        Assert.AreEqual(seats.ToString(), record.GetStructuredStateValue("Seats"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        AssertSingleUntaggedMeasurement(app, "club.waitlist.left", seats);
        AssertNoRejection(app);
    }

    [TestMethod]
    public async Task Leave_Promoted_LogsRejectionWithoutLeftEvent()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, user.Id, classId, 1, "promoted");
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await LeaveAsync(client, id);

        await Enrollments.EnrollApi.AssertConflictAsync(response, "already-promoted");
        AssertRejected(app, "leave-waitlist", "already-promoted", user);
        AssertNoLeave(app);
    }

    [TestMethod]
    public async Task Leave_ClassLockTimeout_LogsTransactionConflictWithoutLeftEvent()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync();
        var classId = await FullClassAsync(app);
        var id = await EntryAsync(app, user.Id, classId, 1);
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HoldClassAsync(app, classId);

        using var response = await LeaveAsync(client, id);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertLockTimeoutObserved(app, "leave-waitlist", user);
        AssertNoLeave(app);
    }

    private static FakeLogRecord One(IReadOnlyList<FakeLogRecord> records)
    {
        Assert.AreEqual(1, records.Count);

        return records[0];
    }

    private static void AssertRejected(TestApp app, string operation, string reason, TestUser user)
    {
        var record = One(app.Logs.GetSnapshot().Where(x => x.Id.Id == 1001).ToList());
        Assert.AreEqual("RequestRejected", record.Id.Name);
        Assert.AreEqual(operation, record.GetStructuredStateValue("Operation"));
        Assert.AreEqual(reason, record.GetStructuredStateValue("Reason"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        AssertRejectionCounted(app, operation, reason);
    }

    private static void AssertLockTimeoutObserved(TestApp app, string operation, TestUser user)
    {
        var record = One(app.Logs.GetSnapshot().Where(x => x.Id.Id == 1002).ToList());
        Assert.AreEqual("TransactionConflict", record.Id.Name);
        Assert.AreEqual(operation, record.GetStructuredStateValue("Operation"));
        Assert.AreEqual("lock-timeout", record.GetStructuredStateValue("Reason"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        Assert.AreEqual(0, app.Logs.GetSnapshot().Count(x => x.Id.Id == 1001));
        AssertRejectionCounted(app, operation, "lock-timeout");
    }

    private static void AssertRejectionCounted(TestApp app, string operation, string reason)
    {
        var measurements = app.Metrics("club.requests.rejected").GetMeasurementSnapshot();
        Assert.AreEqual(1, measurements.Count);
        Assert.AreEqual(1, measurements[0].Value);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, object?> { ["operation"] = operation, ["reason"] = reason },
            measurements[0].Tags.ToDictionary(x => x.Key, x => x.Value));
    }

    private static void AssertNoRejection(TestApp app)
    {
        Assert.AreEqual(0, app.Logs.GetSnapshot().Count(x => x.Id.Id is 1001 or 1002));
        Assert.AreEqual(0, app.Metrics("club.requests.rejected").GetMeasurementSnapshot().Count);
    }

    private static void AssertNoJoin(TestApp app)
    {
        Assert.AreEqual(0, JoinedEvents(app).Count);
        Assert.AreEqual(0, ReplayEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.waitlist.joined").GetMeasurementSnapshot().Count);
    }

    private static void AssertNoLeave(TestApp app)
    {
        Assert.AreEqual(0, LeftEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.waitlist.left").GetMeasurementSnapshot().Count);
    }

    private static void AssertSingleUntaggedMeasurement(TestApp app, string instrument, long value)
    {
        var measurements = app.Metrics(instrument).GetMeasurementSnapshot();

        Assert.AreEqual(1, measurements.Count, instrument);
        Assert.AreEqual(value, measurements[0].Value, instrument);
        Assert.AreEqual(0, measurements[0].Tags.Count, instrument);
    }

    private static IReadOnlyList<FakeLogRecord> ReplayEvents(TestApp app)
    {
        return app.Logs.GetSnapshot().Where(x => x.Id.Id == 1004).ToList();
    }

    private static Task<HttpResponseMessage> LeaveAsync(HttpClient client, long id)
    {
        return client.PostAsync($"/enrollments/waitlist/{id}/cancel", null);
    }

    private static Task<HeldLock> HoldClassAsync(TestApp app, long classId)
    {
        return HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");
    }
}
