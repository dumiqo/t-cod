using System.Text;
using Microsoft.EntityFrameworkCore;
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

    public static async Task AssertNoTraceAsync(TestApp app, TestUser user, int remainingVisits)
    {
        await using var context = app.CreateContext();

        Assert.IsFalse(await context.WaitlistEntries.AnyAsync(x => x.UserId == user.Id));
        Assert.IsFalse(await context.Enrollments.AnyAsync(x => x.UserId == user.Id));
        Assert.IsFalse(await context.IdempotencyKeys.AnyAsync(x => x.UserId == user.Id));
        Assert.AreEqual(remainingVisits, await EnrollApi.RemainingVisitsAsync(app, user));
    }
}
