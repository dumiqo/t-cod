using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.CancelApi;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class CancelEnrollmentObservabilityTests
{
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    [TestMethod]
    [DataRow(10, 3, 13, 1L)]
    [DataRow(10, 3, 13, 0L)]
    [DataRow(0, int.MaxValue, int.MaxValue, 1L)]
    public async Task Cancel_TwoHoursOrMoreBeforeStart_LogsEnrollmentCancelledAndCountsRefund(
        int visits,
        int seats,
        int remaining,
        long ticksPastTwoHours)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: visits);
        var classId = await app.Data.ClassAsync(capacity: int.MaxValue, startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, seats);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt - TwoHours - TimeSpan.FromTicks(ticksPastTwoHours));

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, seats);
        var record = One(RefundedEvents(app));
        Assert.AreEqual("EnrollmentCancelled", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        AssertEventFields(record, id, classId, seats, user, remaining);
        Assert.AreEqual(seats.ToString(), record.GetStructuredStateValue("RefundedVisits"));
        Assert.AreEqual($"/enrollments/{id}/cancel", ScopeValue(record, "RequestPath"));
        AssertSingleUntaggedRefund(app, seats);
        Assert.AreEqual(0, NotRefundedEvents(app).Count);
        AssertNoRejection(app);
    }

    [TestMethod]
    [DataRow(1L)]
    [DataRow(TimeSpan.TicksPerHour)]
    public async Task Cancel_ClassCancelledByClubAfterStart_LogsEnrollmentCancelledWithFullRefund(long ticksAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: startsAt, cancelled: true);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt + TimeSpan.FromTicks(ticksAfterStart));

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 3);
        var record = One(RefundedEvents(app));
        AssertEventFields(record, id, classId, 3, user, 7);
        Assert.AreEqual("3", record.GetStructuredStateValue("RefundedVisits"));
        AssertSingleUntaggedRefund(app, 3);
        Assert.AreEqual(0, NotRefundedEvents(app).Count);
        AssertNoRejection(app);
    }

    [TestMethod]
    [DataRow(1L)]
    [DataRow(TimeSpan.TicksPerHour)]
    public async Task Cancel_LessThanTwoHoursBeforeStart_LogsCancelledWithoutRefundAndCountsNothing(long ticksInsideTwoHours)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt - TwoHours + TimeSpan.FromTicks(ticksInsideTwoHours));

        using var response = await CancelAsync(client, id);

        await AssertCancelledAsync(response, id, 0);
        var record = One(NotRefundedEvents(app));
        Assert.AreEqual("EnrollmentCancelledWithoutRefund", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        AssertEventFields(record, id, classId, 3, user, 4);
        Assert.IsNull(record.GetStructuredStateValue("RefundedVisits"));
        Assert.AreEqual(0, RefundedEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.visits.refunded").GetMeasurementSnapshot().Count);
        AssertNoRejection(app);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Cancel_Repeated_LogsAndCountsNothingMore(bool refunded)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync(startsAt: startsAt);
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);
        if (!refunded)
        {
            app.Clock.SetUtcNow(startsAt - TimeSpan.FromHours(1));
        }

        using var first = await CancelAsync(client, id);
        await AssertCancelledAsync(first, id, refunded ? 3 : 0);
        var logged = app.Logs.GetSnapshot().Count(x => x.Id.Id is 2002 or 2003);
        var measured = app.Metrics("club.visits.refunded").GetMeasurementSnapshot().Count;

        using var repeat = await CancelAsync(client, id);

        await AssertCancelledAsync(repeat, id, refunded ? 3 : 0);
        Assert.AreEqual(1, logged);
        Assert.AreEqual(refunded ? 1 : 0, measured);
        Assert.AreEqual(refunded ? 1 : 0, RefundedEvents(app).Count);
        Assert.AreEqual(refunded ? 0 : 1, NotRefundedEvents(app).Count);
        Assert.AreEqual(measured, app.Metrics("club.visits.refunded").GetMeasurementSnapshot().Count);
        Assert.AreEqual(0, ReplayEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.idempotency.replayed").GetMeasurementSnapshot().Count);
        AssertNoRejection(app);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    public async Task Cancel_ClassStarted_LogsRejectionWithoutSuccessEventOrCounts(long ticksAfterStart)
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var id = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(startsAt: startsAt), 3);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt + TimeSpan.FromTicks(ticksAfterStart));

        using var response = await CancelAsync(client, id);

        await AssertConflictAsync(response, "class-started");
        var record = One(RejectedEvents(app));
        Assert.AreEqual("RequestRejected", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual("cancel-enrollment", record.GetStructuredStateValue("Operation"));
        Assert.AreEqual("class-started", record.GetStructuredStateValue("Reason"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        var measurement = One(app.Metrics("club.requests.rejected").GetMeasurementSnapshot());
        Assert.AreEqual(1, measurement.Value);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, object?> { ["operation"] = "cancel-enrollment", ["reason"] = "class-started" },
            measurement.Tags.ToDictionary(x => x.Key, x => x.Value));
        AssertNoSuccess(app);
        await AssertNoCancelTraceAsync(app, id, user, 4);
    }

    [TestMethod]
    public async Task Cancel_NotFoundOrForeign_Returns404WithoutEventsOrCounts()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var other = await app.Data.UserAsync(remainingVisits: 4);
        var id = await app.Data.EnrollmentAsync(other.Id, await app.Data.ClassAsync(), 3);
        using var client = await app.CreateClientAsync(user.Email);

        using var foreign = await CancelAsync(client, id);
        using var missing = await CancelAsync(client, long.MaxValue);

        await AssertNotFoundAsync(foreign);
        await AssertNotFoundAsync(missing);
        AssertNothingObserved(app);
        await AssertNoCancelTraceAsync(app, id, other, 4);
    }

    [TestMethod]
    public async Task Cancel_NonNumericId_Returns400WithoutEventsOrCounts()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 4);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await CancelAsync(client, "abc");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertNothingObserved(app);
    }

    [TestMethod]
    public async Task Cancel_ClassLockTimeout_Returns503WithoutSuccessEventOrCounts()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var classId = await app.Data.ClassAsync();
        var id = await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HeldLock.AcquireAsync(
            app.ConnectionString,
            $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");

        using var response = await CancelAsync(client, id);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertNoSuccess(app);
        Assert.AreEqual(0, RejectedEvents(app).Count);
        await AssertNoCancelTraceAsync(app, id, user, 4);
    }

    [TestMethod]
    public async Task Cancel_CancelledAndRejected_LogsNoEmail()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = Now(app).AddDays(1);
        var user = await app.Data.UserAsync(remainingVisits: 4);
        var refunded = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(startsAt: startsAt.AddDays(1)), 1);
        var notRefunded = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(startsAt: startsAt.AddHours(1)), 1);
        var started = await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(startsAt: startsAt), 1);
        using var client = await app.CreateClientAsync(user.Email);
        app.Clock.SetUtcNow(startsAt);

        using var first = await CancelAsync(client, refunded);
        using var second = await CancelAsync(client, notRefunded);
        using var third = await CancelAsync(client, started);

        await AssertCancelledAsync(first, refunded, 1);
        await AssertCancelledAsync(second, notRefunded, 0);
        await AssertConflictAsync(third, "class-started");
        Assert.AreEqual(1, RefundedEvents(app).Count);
        Assert.AreEqual(1, NotRefundedEvents(app).Count);
        Assert.AreEqual(1, RejectedEvents(app).Count);
        var texts = app.Logs.GetSnapshot().Select(LogText).ToList();
        Assert.IsFalse(texts.Any(x => x.Contains(user.Email, StringComparison.OrdinalIgnoreCase)), "Log contains email.");
    }

    private static void AssertEventFields(FakeLogRecord record, long id, long classId, int seats, TestUser user, int remaining)
    {
        Assert.AreEqual(id.ToString(), record.GetStructuredStateValue("EnrollmentId"));
        Assert.AreEqual(classId.ToString(), record.GetStructuredStateValue("ClassId"));
        Assert.AreEqual(seats.ToString(), record.GetStructuredStateValue("Seats"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        Assert.AreEqual(remaining.ToString(), record.GetStructuredStateValue("RemainingVisits"));
    }

    private static void AssertSingleUntaggedRefund(TestApp app, long value)
    {
        var measurement = One(app.Metrics("club.visits.refunded").GetMeasurementSnapshot());

        Assert.AreEqual(value, measurement.Value);
        Assert.AreEqual(0, measurement.Tags.Count);
    }

    private static void AssertNoSuccess(TestApp app)
    {
        Assert.AreEqual(0, RefundedEvents(app).Count);
        Assert.AreEqual(0, NotRefundedEvents(app).Count);
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

    private static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }

    private static string LogText(FakeLogRecord record)
    {
        var state = record.StructuredState?.Select(x => $"{x.Key}={x.Value}") ?? [];
        var scopes = record.Scopes.SelectMany(scope => scope is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.Select(x => $"{x.Key}={x.Value}")
            : [scope?.ToString()]);

        return string.Join("\n", new[] { record.Message, record.Exception?.ToString() }.Concat(state).Concat(scopes));
    }

    private static string? ScopeValue(FakeLogRecord record, string key)
    {
        return record.Scopes
            .OfType<IEnumerable<KeyValuePair<string, object?>>>()
            .SelectMany(x => x)
            .Where(x => x.Key == key)
            .Select(x => x.Value?.ToString())
            .FirstOrDefault();
    }
}
