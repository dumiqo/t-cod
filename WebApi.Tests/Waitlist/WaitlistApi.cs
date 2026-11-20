using System.Text;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Enrollments;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Waitlist;

public static class WaitlistApi
{
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string json)
    {
        return PostAsync(client, json, Guid.NewGuid().ToString());
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string json, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments/waitlist")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> JoinAsync(HttpClient client, long classId, int seats)
    {
        return PostAsync(client, $"{{\"classId\":{classId},\"seats\":{seats}}}");
    }

    public static async Task<long> FullClassAsync(TestApp app, int capacity = 2)
    {
        var filler = await app.Data.UserAsync(remainingVisits: capacity);
        var classId = await app.Data.ClassAsync(capacity: capacity);
        await app.Data.EnrollmentAsync(filler.Id, classId, capacity);

        return classId;
    }

    public static async Task<long> EntryAsync(TestApp app, long userId, long classId, int seats, string status = "waiting")
    {
        var entry = new WaitlistEntry
        {
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            Status = status,
            CreatedAt = app.Clock.GetUtcNow().UtcDateTime
        };

        await using var context = app.CreateContext();
        context.WaitlistEntries.Add(entry);
        await context.SaveChangesAsync();

        return entry.Id;
    }

    public static async Task<string[]> WaitlistSnapshotAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();
        var entries = await context.WaitlistEntries.AsNoTracking()
            .Where(x => x.ClassId == classId)
            .OrderBy(x => x.Id)
            .ToListAsync();

        return entries.Select(x => $"{x.Id}|{x.UserId}|{x.Seats}|{x.Status}|{x.CreatedAt:O}|{x.CancelledAt:O}").ToArray();
    }

    public static async Task AssertNoTraceAsync(TestApp app, TestUser user, int remainingVisits)
    {
        await using var context = app.CreateContext();

        Assert.IsFalse(await context.WaitlistEntries.AnyAsync(x => x.UserId == user.Id));
        Assert.IsFalse(await context.Enrollments.AnyAsync(x => x.UserId == user.Id));
        Assert.IsFalse(await context.IdempotencyKeys.AnyAsync(x => x.UserId == user.Id));
        Assert.AreEqual(remainingVisits, await EnrollApi.RemainingVisitsAsync(app, user));
        var joined = JoinedEvents(app);
        Assert.IsFalse(joined.Any(x => x.GetStructuredStateValue("UserId") == user.Id.ToString()));
        Assert.AreEqual(
            joined.Sum(x => long.Parse(x.GetStructuredStateValue("Seats") ?? string.Empty)),
            EnrollApi.MetricSum(app, "club.waitlist.joined"));
    }

    public static IReadOnlyList<FakeLogRecord> JoinedEvents(TestApp app)
    {
        return app.Logs.GetSnapshot().Where(x => x.Id.Id == 3001).ToList();
    }

    public static IReadOnlyList<FakeLogRecord> LeftEvents(TestApp app)
    {
        return app.Logs.GetSnapshot().Where(x => x.Id.Id == 3002).ToList();
    }
}
