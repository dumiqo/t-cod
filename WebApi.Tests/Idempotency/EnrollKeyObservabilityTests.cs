using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Idempotency;

[TestClass]
public class EnrollKeyObservabilityTests
{
    [TestMethod]
    [DataRow("none")]
    [DataRow("class-started")]
    [DataRow("class-cancelled")]
    public async Task Enroll_Replay_LogsIdempotentReplayAndCountsWithoutCharge(string afterwards)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":3}}";

        using var first = await PostAsync(client, json, "key-1");
        var firstText = await first.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, firstText);
        await ApplyAsync(app, classId, afterwards);
        using var repeat = await PostAsync(client, json, "key-1");

        var text = await repeat.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, text);
        Assert.AreEqual(firstText, text);
        using var body = JsonDocument.Parse(text);
        var replays = ReplayEvents(app);
        Assert.AreEqual(1, replays.Count);
        var record = replays[0];
        Assert.AreEqual("IdempotentReplay", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual("enroll", record.GetStructuredStateValue("Operation"));
        Assert.AreEqual(body.RootElement.GetProperty("id").GetInt64().ToString(), record.GetStructuredStateValue("ResultId"));
        Assert.AreEqual(classId.ToString(), record.GetStructuredStateValue("ClassId"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        AssertReplaysCounted(app, 1);
        Assert.AreEqual(1, CreatedEvents(app).Count);
        AssertSingleMeasurement(app, "club.enrollments.created", 3);
        AssertSingleMeasurement(app, "club.visits.charged", 3);
        Assert.AreEqual(0, RejectedEvents(app).Count);
        Assert.AreEqual(0, app.Metrics("club.requests.rejected").GetMeasurementSnapshot().Count);
        Assert.AreEqual(7, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    [DataRow(true, 1)]
    [DataRow(false, 2)]
    public async Task Enroll_KeyReusedWithOtherBody_LogsRejectionWithoutReplay(bool otherClass, int seats)
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        var otherClassId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var first = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", "key-1");
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
        using var reused = await PostAsync(
            client,
            $"{{\"classId\":{(otherClass ? otherClassId : classId)},\"seats\":{seats}}}",
            "key-1");

        await AssertConflictAsync(reused, "idempotency-key-reused");
        var events = RejectedEvents(app);
        Assert.AreEqual(1, events.Count);
        var record = events[0];
        Assert.AreEqual("RequestRejected", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual("enroll", record.GetStructuredStateValue("Operation"));
        Assert.AreEqual("idempotency-key-reused", record.GetStructuredStateValue("Reason"));
        Assert.AreEqual(user.Id.ToString(), record.GetStructuredStateValue("UserId"));
        AssertRejectionsCounted(app, "idempotency-key-reused", 1);
        Assert.AreEqual(0, ReplayEvents(app).Count);
        AssertReplaysCounted(app, 0);
        Assert.AreEqual(1, CreatedEvents(app).Count);
        AssertSingleMeasurement(app, "club.visits.charged", 1);
        Assert.AreEqual(9, await RemainingVisitsAsync(app, user));
    }

    [TestMethod]
    public async Task Enroll_NewKeyAfterReplay_IsNotCountedAsReplay()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";

        using var first = await PostAsync(client, json, "key-1");
        using var repeat = await PostAsync(client, json, "key-1");
        using var second = await PostAsync(client, json, "key-2");
        using var secondRepeat = await PostAsync(client, json, "key-2");

        foreach (var response in new[] { first, repeat, second, secondRepeat })
        {
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        await using var context = app.CreateContext();
        var ids = await context.Enrollments.Where(x => x.UserId == user.Id).OrderBy(x => x.Id).Select(x => x.Id.ToString()).ToListAsync();
        Assert.AreEqual(2, ids.Count);
        CollectionAssert.AreEqual(ids, ReplayEvents(app).Select(x => x.GetStructuredStateValue("ResultId")).ToList());
        AssertReplaysCounted(app, 2);
        Assert.AreEqual(2, CreatedEvents(app).Count);
        Assert.AreEqual(2, MetricSum(app, "club.visits.charged"));
    }

    [TestMethod]
    public async Task Enroll_CreatedReplayedReusedAnd503_LogsNoIdempotencyKey()
    {
        await using var app = await TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", "00:00:00.300"));
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        var heldClassId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var key = $"secret-{Guid.NewGuid():N}";
        var timeoutKey = $"timeout-{Guid.NewGuid():N}";

        using var created = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", key);
        using var replayed = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":1}}", key);
        using var reused = await PostAsync(client, $"{{\"classId\":{classId},\"seats\":2}}", key);
        await using (await HeldLock.AcquireAsync(
            app.ConnectionString,
            $"SELECT 1 FROM classes WHERE id = {heldClassId} FOR NO KEY UPDATE"))
        {
            using var unavailable = await PostAsync(client, $"{{\"classId\":{heldClassId},\"seats\":1}}", timeoutKey);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode, await unavailable.Content.ReadAsStringAsync());
        }

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Created, replayed.StatusCode, await replayed.Content.ReadAsStringAsync());
        await AssertConflictAsync(reused, "idempotency-key-reused");
        var records = app.Logs.GetSnapshot();
        Assert.AreEqual(1, records.Count(x => x.Id.Id == 2001));
        Assert.AreEqual(1, records.Count(x => x.Id.Id == 1004));
        Assert.AreEqual(1, records.Count(x => x.Id.Id == 1001));
        Assert.AreEqual(1, records.Count(x => x.Id.Id == 1002));
        var texts = records.Select(LogText).ToList();
        foreach (var secret in new[] { key, timeoutKey })
        {
            Assert.IsFalse(
                texts.Any(x => x.Contains(secret, StringComparison.OrdinalIgnoreCase)),
                $"Log contains {secret}.");
        }
    }

    private static async Task ApplyAsync(TestApp app, long classId, string afterwards)
    {
        await using var context = app.CreateContext();

        switch (afterwards)
        {
            case "class-started":
                app.Clock.Advance(TimeSpan.FromDays(2));
                break;
            case "class-cancelled":
                await context.Database.ExecuteSqlAsync($"UPDATE classes SET is_cancelled = TRUE WHERE id = {classId}");
                break;
        }
    }

    private static void AssertReplaysCounted(TestApp app, int count)
    {
        var measurements = app.Metrics("club.idempotency.replayed").GetMeasurementSnapshot();

        Assert.AreEqual(count, measurements.Count);
        foreach (var measurement in measurements)
        {
            Assert.AreEqual(1, measurement.Value);
            CollectionAssert.AreEquivalent(
                new Dictionary<string, object?> { ["operation"] = "enroll" },
                measurement.Tags.ToDictionary(x => x.Key, x => x.Value));
        }
    }

    private static void AssertSingleMeasurement(TestApp app, string instrument, long value)
    {
        var measurements = app.Metrics(instrument).GetMeasurementSnapshot();

        Assert.AreEqual(1, measurements.Count, instrument);
        Assert.AreEqual(value, measurements[0].Value, instrument);
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
