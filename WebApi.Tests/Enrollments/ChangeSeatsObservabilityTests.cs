using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.ChangeSeatsApi;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class ChangeSeatsObservabilityTests
{
    [TestMethod]
    [DataRow(2, 3, 5, 4)]
    [DataRow(1, 2, 1, 0)]
    [DataRow(1, int.MaxValue, int.MaxValue - 1, 0)]
    public async Task ChangeSeats_Increase_LogsSeatsIncreasedAndCountsChargedDifference(int oldSeats, int newSeats, int visits, int remaining)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: visits);
        var classId = await app.Data.ClassAsync(capacity: int.MaxValue);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, oldSeats);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, newSeats);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var record = One(IncreasedEvents(app));
        Assert.AreEqual("EnrollmentSeatsIncreased", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        AssertEventFields(record, id, classId, oldSeats, newSeats, user, remaining);
        Assert.AreEqual((newSeats - oldSeats).ToString(), record.GetStructuredStateValue("ChargedVisits"));
        Assert.IsNull(record.GetStructuredStateValue("RefundedVisits"));
        AssertSingleUntagged(app, "club.visits.charged", newSeats - oldSeats);
        Assert.AreEqual(0, DecreasedEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.visits.refunded").GetMeasurementSnapshot().Count);
        Assert.AreEqual(0, app.Metrics("club.enrollments.created").GetMeasurementSnapshot().Count);
        Assert.AreEqual(0, CreatedEvents(app).Count);
        AssertNoRejection(app);
    }

    [TestMethod]
    [DataRow(5, 2, 0, 3)]
    [DataRow(2, 1, 4, 5)]
    [DataRow(int.MaxValue, 1, 0, int.MaxValue - 1)]
    public async Task ChangeSeats_Decrease_LogsSeatsDecreasedAndCountsRefundedDifference(int oldSeats, int newSeats, int visits, int remaining)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: visits);
        var classId = await app.Data.ClassAsync(capacity: int.MaxValue);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, oldSeats);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, newSeats);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var record = One(DecreasedEvents(app));
        Assert.AreEqual("EnrollmentSeatsDecreased", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        AssertEventFields(record, id, classId, oldSeats, newSeats, user, remaining);
        Assert.AreEqual((oldSeats - newSeats).ToString(), record.GetStructuredStateValue("RefundedVisits"));
        Assert.IsNull(record.GetStructuredStateValue("ChargedVisits"));
        AssertSingleUntagged(app, "club.visits.refunded", oldSeats - newSeats);
        Assert.AreEqual(0, IncreasedEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.visits.charged").GetMeasurementSnapshot().Count);
        Assert.AreEqual(0, RefundedEvents(app).Count);
        AssertNoRejection(app);
    }

    [TestMethod]
    public async Task ChangeSeats_SameSeats_LogsAndCountsNothing()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 3);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 3);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertNothingObserved(app);
    }

    [TestMethod]
    [DataRow("enrollment-cancelled")]
    [DataRow("class-cancelled")]
    [DataRow("class-started")]
    [DataRow("pass-expired")]
    [DataRow("pass-exhausted")]
    [DataRow("capacity-exceeded")]
    public async Task ChangeSeats_Rejected_LogsRejectionWithoutSuccessEventOrCounts(string reason)
    {
        await using var app = await TestApp.StartAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var user = await app.Data.UserAsync(
            remainingVisits: reason == "pass-exhausted" ? 1 : 10,
            validUntil: DateOnly.FromDateTime(now).AddDays(reason == "pass-expired" ? -1 : 30));
        var classId = await app.Data.ClassAsync(
            capacity: reason == "capacity-exceeded" ? 2 : 10,
            startsAt: reason == "class-started" ? now.AddHours(-1) : now.AddDays(1),
            cancelled: reason == "class-cancelled");
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2, cancelled: reason == "enrollment-cancelled");
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsAsync(client, id, 4);

        await AssertConflictAsync(response, reason);
        var record = One(RejectedEvents(app));
        Assert.AreEqual("RequestRejected", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual("change-seats", record.GetStructuredStateValue("Operation"));
        Assert.AreEqual(reason, record.GetStructuredStateValue("Reason"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        var measurement = One(app.Metrics("club.requests.rejected").GetMeasurementSnapshot());
        Assert.AreEqual(1, measurement.Value);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, object?> { ["operation"] = "change-seats", ["reason"] = reason },
            measurement.Tags.ToDictionary(x => x.Key, x => x.Value));
        AssertNoSuccess(app);
    }

    [TestMethod]
    public async Task ChangeSeats_NotFoundOrForeign_Returns404WithoutEventsOrCounts()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var other = await app.Data.UserAsync(remainingVisits: 4);
        var id = await app.Data.EnrollmentAsync(other.Id, await app.Data.ClassAsync(), 3);
        using var client = await app.CreateClientAsync(user.Email);

        using var foreign = await ChangeSeatsAsync(client, id, 1);
        using var missing = await ChangeSeatsAsync(client, long.MaxValue, 1);

        await AssertNotFoundAsync(foreign);
        await AssertNotFoundAsync(missing);
        AssertNothingObserved(app);
        await AssertNoChangeTraceAsync(app, other, id, 3, 4);
    }

    [TestMethod]
    [DataRow("abc", "{\"seats\":1}")]
    [DataRow("1", "{\"seats\":0}")]
    public async Task ChangeSeats_InvalidInput_Returns400WithoutEventsOrCounts(string id, string json)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await ChangeSeatsApi.PostAsync(client, id, json);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertNothingObserved(app);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task ChangeSeats_ClassLockTimeout_Returns503WithoutSuccessEventOrCounts(int seats)
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HeldLock.AcquireAsync(
            app.ConnectionString,
            $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");

        using var response = await ChangeSeatsAsync(client, id, seats);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertNoSuccess(app);
        Assert.AreEqual(0, RejectedEvents(app).Count);
        await AssertNoChangeTraceAsync(app, user, id, 2, 4);
    }

    [TestMethod]
    public async Task ChangeSeats_ChangedAndRejected_LogsNoEmail()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(capacity: 5);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var increased = await ChangeSeatsAsync(client, id, 3);
        using var decreased = await ChangeSeatsAsync(client, id, 1);
        using var rejected = await ChangeSeatsAsync(client, id, 6);

        Assert.AreEqual(HttpStatusCode.OK, increased.StatusCode, await increased.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, decreased.StatusCode, await decreased.Content.ReadAsStringAsync());
        await AssertConflictAsync(rejected, "capacity-exceeded");
        Assert.AreEqual(1, IncreasedEvents(app).Count);
        Assert.AreEqual(1, DecreasedEvents(app).Count);
        Assert.AreEqual(1, RejectedEvents(app).Count);
        var texts = app.Logs.GetSnapshot().Select(LogText).ToList();
        Assert.IsFalse(texts.Any(x => x.Contains(user.Email, StringComparison.OrdinalIgnoreCase)), "Log contains email.");
    }

    [TestMethod]
    public async Task ChangeSeats_DecreaseThenCancelWithinTwoHours_LogsDecreasedThenCancelledWithoutRefund()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 0);
        var classId = await app.Data.ClassAsync(startsAt: app.Clock.GetUtcNow().UtcDateTime.AddHours(1));
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 5);
        using var client = await app.CreateClientAsync(user.Email);

        using var change = await ChangeSeatsAsync(client, id, 1);
        using var cancel = await CancelAsync(client, id);

        Assert.AreEqual(HttpStatusCode.OK, change.StatusCode, await change.Content.ReadAsStringAsync());
        await AssertCancelledAsync(cancel, id, 0);
        var events = app.Logs.GetSnapshot().Where(x => x.Id.Id is 2005 or 2003).ToList();
        CollectionAssert.AreEqual(new[] { 2005, 2003 }, events.Select(x => x.Id.Id).ToArray());
        AssertEventFields(events[0], id, classId, 5, 1, user, 4);
        Assert.AreEqual("4", events[0].GetStructuredStateValue("RefundedVisits"));
        Assert.AreEqual(id.ToString(), events[1].GetStructuredStateValue("EnrollmentId"));
        Assert.AreEqual("1", events[1].GetStructuredStateValue("Seats"));
        Assert.AreEqual("4", events[1].GetStructuredStateValue("RemainingVisits"));
        AssertSingleUntagged(app, "club.visits.refunded", 4);
    }

    private static void AssertEventFields(
        FakeLogRecord record,
        long id,
        long classId,
        int oldSeats,
        int newSeats,
        TestUser user,
        int remaining)
    {
        Assert.AreEqual(id.ToString(), record.GetStructuredStateValue("EnrollmentId"));
        Assert.AreEqual(classId.ToString(), record.GetStructuredStateValue("ClassId"));
        Assert.AreEqual(oldSeats.ToString(), record.GetStructuredStateValue("OldSeats"));
        Assert.AreEqual(newSeats.ToString(), record.GetStructuredStateValue("NewSeats"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        Assert.AreEqual(remaining.ToString(), record.GetStructuredStateValue("RemainingVisits"));
    }

    private static void AssertSingleUntagged(TestApp app, string instrument, long value)
    {
        var measurement = One(app.Metrics(instrument).GetMeasurementSnapshot());

        Assert.AreEqual(value, measurement.Value);
        Assert.AreEqual(0, measurement.Tags.Count);
    }

    private static void AssertNoSuccess(TestApp app)
    {
        Assert.AreEqual(0, IncreasedEvents(app).Count);
        Assert.AreEqual(0, DecreasedEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.visits.charged").GetMeasurementSnapshot().Count);
        Assert.AreEqual(0, app.Metrics("club.visits.refunded").GetMeasurementSnapshot().Count);
    }

    private static void AssertNoRejection(TestApp app)
    {
        Assert.AreEqual(0, RejectedEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.requests.rejected").GetMeasurementSnapshot().Count);
    }

    private static void AssertNothingObserved(TestApp app)
    {
        AssertNoSuccess(app);
        AssertNoRejection(app);
    }

    private static T One<T>(IReadOnlyList<T> items)
    {
        Assert.AreEqual(1, items.Count);

        return items[0];
    }

    private static string LogText(FakeLogRecord record)
    {
        var state = record.StructuredState?.Select(x => $"{x.Key}={x.Value}") ?? [];
        var scopes = record.Scopes.SelectMany(scope => scope is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.Select(x => $"{x.Key}={x.Value}")
            : [scope?.ToString()]);

        return string.Join("\n", new[] { record.Message, record.Exception?.ToString() }.Concat(state).Concat(scopes));
    }
}
