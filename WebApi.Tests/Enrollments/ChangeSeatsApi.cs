using System.Text;
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

    public static async Task AssertNoChangeTraceAsync(TestApp app, TestUser user, long enrollmentId, int seats, int remainingVisits)
    {
        var enrollment = await CancelApi.EnrollmentAsync(app, enrollmentId);

        Assert.AreEqual(seats, enrollment.Seats);
        Assert.AreEqual("active", enrollment.Status);
        Assert.IsNull(enrollment.CancelledAt);
        Assert.IsNull(enrollment.RefundedVisits);
        Assert.AreEqual(remainingVisits, await EnrollApi.RemainingVisitsAsync(app, user));
    }
}
