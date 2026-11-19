using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Enrollments;

public static class EnrollApi
{
    public const string TraceParent = "^00-[0-9a-f]{32}-[0-9a-f]{16}-00$";

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string json, string? key = "key")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> EnrollAsync(HttpClient client, long classId, int seats)
    {
        return PostAsync(client, $"{{\"classId\":{classId},\"seats\":{seats}}}");
    }

    public static async Task AssertConflictAsync(HttpResponseMessage response, string reason)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, text);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        CollectionAssert.AreEqual(
            new[] { "reason", "status", "title", "traceId", "type" },
            root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.AreEqual(409, root.GetProperty("status").GetInt32());
        Assert.AreEqual("Conflict", root.GetProperty("title").GetString());
        Assert.AreEqual("https://tools.ietf.org/html/rfc9110#section-15.5.10", root.GetProperty("type").GetString());
        Assert.AreEqual(reason, root.GetProperty("reason").GetString());
        StringAssert.Matches(root.GetProperty("traceId").GetString(), new Regex(TraceParent));
    }

    public static async Task<int> RemainingVisitsAsync(TestApp app, TestUser user)
    {
        await using var context = app.CreateContext();

        return await context.Passes.Where(x => x.UserId == user.Id).Select(x => x.RemainingVisits).SingleAsync();
    }

    public static async Task AssertNoTraceAsync(TestApp app, TestUser user, int remainingVisits)
    {
        await using var context = app.CreateContext();

        Assert.IsFalse(await context.Enrollments.AnyAsync(x => x.UserId == user.Id));
        Assert.AreEqual(remainingVisits, await RemainingVisitsAsync(app, user));
        var events = CreatedEvents(app);
        Assert.IsFalse(events.Any(x => x.GetStructuredStateValue("UserId") == user.Id.ToString()));
        var seats = events.Sum(x => long.Parse(x.GetStructuredStateValue("Seats") ?? string.Empty));
        Assert.AreEqual(seats, MetricSum(app, "club.enrollments.created"));
        Assert.AreEqual(seats, MetricSum(app, "club.visits.charged"));
    }

    public static IReadOnlyList<FakeLogRecord> CreatedEvents(TestApp app)
    {
        return app.Logs.GetSnapshot().Where(x => x.Id.Id == 2001).ToList();
    }

    public static IReadOnlyList<FakeLogRecord> RejectedEvents(TestApp app)
    {
        return app.Logs.GetSnapshot().Where(x => x.Id.Id == 1001).ToList();
    }

    public static long MetricSum(TestApp app, string instrument)
    {
        return app.Metrics(instrument).GetMeasurementSnapshot().Sum(x => x.Value);
    }

    public static void AssertRejectionsCounted(TestApp app, string reason, int count)
    {
        var measurements = app.Metrics("club.requests.rejected").GetMeasurementSnapshot();

        Assert.AreEqual(count, measurements.Count);
        foreach (var measurement in measurements)
        {
            Assert.AreEqual(1, measurement.Value);
            CollectionAssert.AreEquivalent(
                new Dictionary<string, object?> { ["operation"] = "enroll", ["reason"] = reason },
                measurement.Tags.ToDictionary(x => x.Key, x => x.Value));
        }
    }

    public static async Task AssertNoEnrollmentsAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();

        Assert.IsFalse(await context.Enrollments.AnyAsync(x => x.ClassId == classId));
    }

    public static async Task<string[]> EnrollmentsSnapshotAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();
        var enrollments = await context.Enrollments.AsNoTracking()
            .Where(x => x.ClassId == classId)
            .OrderBy(x => x.Id)
            .ToListAsync();

        return enrollments.Select(Snapshot).ToArray();
    }

    public static string Snapshot(Enrollment x)
    {
        return $"{x.Id}|{x.UserId}|{x.ClassId}|{x.Seats}|{x.Status}|{x.CreatedAt:O}|{x.CancelledAt:O}";
    }
}
