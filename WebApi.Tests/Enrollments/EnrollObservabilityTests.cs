using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class EnrollObservabilityTests
{
    [TestMethod]
    [DataRow(10, 3, 7)]
    [DataRow(int.MaxValue, int.MaxValue, 0)]
    public async Task Enroll_Valid_LogsEnrollmentCreatedAndCountsSeats(int visits, int seats, int remaining)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: visits);
        var classId = await app.Data.ClassAsync(capacity: int.MaxValue);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, seats);

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, text);
        using var body = JsonDocument.Parse(text);
        var events = CreatedEvents(app);
        Assert.AreEqual(1, events.Count);
        var record = events[0];
        Assert.AreEqual("EnrollmentCreated", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual(body.RootElement.GetProperty("id").GetInt64().ToString(), record.GetStructuredStateValue("EnrollmentId"));
        Assert.AreEqual(classId.ToString(), record.GetStructuredStateValue("ClassId"));
        Assert.AreEqual(seats.ToString(), record.GetStructuredStateValue("Seats"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        Assert.AreEqual(remaining.ToString(), record.GetStructuredStateValue("RemainingVisits"));
        Assert.AreEqual("/enrollments", ScopeValue(record, "RequestPath"));
        AssertSingleUntaggedMeasurement(app, "club.enrollments.created", seats);
        AssertSingleUntaggedMeasurement(app, "club.visits.charged", seats);
        Assert.AreEqual(0, RejectedEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.requests.rejected").GetMeasurementSnapshot().Count);
    }

    [TestMethod]
    [DataRow("class-cancelled")]
    [DataRow("class-started")]
    [DataRow("pass-expired")]
    [DataRow("pass-exhausted")]
    [DataRow("capacity-exceeded")]
    public async Task Enroll_Rejected_LogsRejectionWithoutSuccessEventOrCounts(string reason)
    {
        await using var app = await TestApp.StartAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var visits = reason == "pass-exhausted" ? 1 : 10;
        var user = await app.Data.UserAsync(
            remainingVisits: visits,
            validUntil: reason == "pass-expired" ? today.AddDays(-1) : today.AddDays(30));
        var classId = await app.Data.ClassAsync(
            capacity: reason == "capacity-exceeded" ? 1 : 10,
            startsAt: reason == "class-started" ? now.AddHours(-1) : now.AddDays(1),
            cancelled: reason == "class-cancelled");
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, classId, 2);

        await AssertConflictAsync(response, reason);
        var events = RejectedEvents(app);
        Assert.AreEqual(1, events.Count);
        var record = events[0];
        Assert.AreEqual("RequestRejected", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual("enroll", record.GetStructuredStateValue("Operation"));
        Assert.AreEqual(reason, record.GetStructuredStateValue("Reason"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        AssertRejectionsCounted(app, reason, 1);
        AssertNoSuccessObserved(app);
        await AssertNoTraceAsync(app, user, visits);
    }

    [TestMethod]
    public async Task Enroll_ClassNotFound_Returns404WithoutEventsOrCounts()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await EnrollAsync(client, long.MaxValue, 1);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertNothingObserved(app);
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Enroll_MissingIdempotencyKey_Returns400WithoutEventsOrCounts()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", null);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertNothingObserved(app);
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Enroll_ClassLockTimeout_Returns503WithoutSuccessEventOrCounts()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        await using var held = await HeldLock.AcquireAsync(
            app.ConnectionString,
            $"SELECT 1 FROM classes WHERE id = {classId} FOR NO KEY UPDATE");

        using var response = await EnrollAsync(client, classId, 1);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, await response.Content.ReadAsStringAsync());
        AssertNoSuccessObserved(app);
        Assert.AreEqual(0, RejectedEvents(app).Count);
        await AssertNoTraceAsync(app, user, 10);
    }

    [TestMethod]
    public async Task Enroll_CreatedAndRejected_LogsNeitherEmailNorIdempotencyKey()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync(capacity: 1);
        using var client = await app.CreateClientAsync(user.Email);
        var createdKey = $"created-{Guid.NewGuid():N}";
        var rejectedKey = $"rejected-{Guid.NewGuid():N}";

        using var created = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", createdKey);
        using var rejected = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", rejectedKey);

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        await AssertConflictAsync(rejected, "capacity-exceeded");
        Assert.AreEqual(1, CreatedEvents(app).Count(x => x.GetStructuredStateValue("UserId") == user.Id.ToString()));
        Assert.AreEqual(1, RejectedEvents(app).Count(x => x.GetStructuredStateValue("UserId") == user.Id.ToString()));
        var texts = app.Logs.GetSnapshot().Select(LogText).ToList();
        foreach (var secret in new[] { user.Email, createdKey, rejectedKey })
        {
            Assert.IsFalse(
                texts.Any(x => x.Contains(secret, StringComparison.OrdinalIgnoreCase)),
                $"Log contains {secret}.");
        }
    }

    private static void AssertSingleUntaggedMeasurement(TestApp app, string instrument, long value)
    {
        var measurements = app.Metrics(instrument).GetMeasurementSnapshot();

        Assert.AreEqual(1, measurements.Count, instrument);
        Assert.AreEqual(value, measurements[0].Value, instrument);
        Assert.AreEqual(0, measurements[0].Tags.Count, instrument);
    }

    private static void AssertNoSuccessObserved(TestApp app)
    {
        Assert.AreEqual(0, CreatedEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.enrollments.created").GetMeasurementSnapshot().Count);
        Assert.AreEqual(0, app.Metrics("club.visits.charged").GetMeasurementSnapshot().Count);
    }

    private static void AssertNothingObserved(TestApp app)
    {
        AssertNoSuccessObserved(app);
        Assert.AreEqual(0, RejectedEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.requests.rejected").GetMeasurementSnapshot().Count);
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
