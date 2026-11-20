using System.Text;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Enrollments;

public static class ChangeSeatsApi
{
    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string id, string json)
    {
        return client.PostAsync($"/enrollments/{id}/seats", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    public static Task<HttpResponseMessage> ChangeSeatsAsync(HttpClient client, long id, int seats)
    {
        return PostAsync(client, id.ToString(), $"{{\"seats\":{seats}}}");
    }

    public static async Task AssertEnrollmentStateAsync(TestApp app, TestUser user, long enrollmentId, int seats, int remainingVisits)
    {
        var enrollment = await CancelApi.EnrollmentAsync(app, enrollmentId);

        Assert.AreEqual(seats, enrollment.Seats);
        Assert.AreEqual("active", enrollment.Status);
        Assert.IsNull(enrollment.CancelledAt);
        Assert.IsNull(enrollment.RefundedVisits);
        Assert.AreEqual(remainingVisits, await EnrollApi.RemainingVisitsAsync(app, user));
    }

    public static async Task AssertNoChangeTraceAsync(TestApp app, TestUser user, long enrollmentId, int seats, int remainingVisits)
    {
        await AssertEnrollmentStateAsync(app, user, enrollmentId, seats, remainingVisits);
        var increased = IncreasedEvents(app);
        var decreased = DecreasedEvents(app);
        var userId = user.Id.ToString();
        Assert.IsFalse(increased.Concat(decreased).Any(x => x.GetStructuredStateValue("UserId") == userId));
        Assert.AreEqual(
            Sum(EnrollApi.CreatedEvents(app), "Seats") + Sum(increased, "ChargedVisits"),
            EnrollApi.MetricSum(app, "club.visits.charged"));
        Assert.AreEqual(
            Sum(CancelApi.RefundedEvents(app), "RefundedVisits") + Sum(decreased, "RefundedVisits"),
            EnrollApi.MetricSum(app, "club.visits.refunded"));
    }

    public static IReadOnlyList<FakeLogRecord> IncreasedEvents(TestApp app)
    {
        return app.Logs.GetSnapshot().Where(x => x.Id.Id == 2004).ToList();
    }

    public static IReadOnlyList<FakeLogRecord> DecreasedEvents(TestApp app)
    {
        return app.Logs.GetSnapshot().Where(x => x.Id.Id == 2005).ToList();
    }

    private static long Sum(IEnumerable<FakeLogRecord> records, string field)
    {
        return records.Sum(x => long.Parse(x.GetStructuredStateValue(field) ?? string.Empty));
    }
}
