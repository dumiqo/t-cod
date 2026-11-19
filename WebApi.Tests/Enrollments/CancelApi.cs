using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Enrollments;

public static class CancelApi
{
    public static Task<HttpResponseMessage> CancelAsync(HttpClient client, long id)
    {
        return client.PostAsync($"/enrollments/{id}/cancel", null);
    }

    public static Task<HttpResponseMessage> CancelAsync(HttpClient client, string id)
    {
        return client.PostAsync($"/enrollments/{id}/cancel", null);
    }

    public static async Task AssertCancelledAsync(HttpResponseMessage response, long id, int refundedVisits)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        var expected = JsonSerializer.Serialize(new { id, status = "cancelled", refundedVisits });
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(text)), $"expected {expected}, got {text}");
    }

    public static async Task AssertNotFoundAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, text);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        CollectionAssert.AreEqual(
            new[] { "status", "title", "traceId", "type" },
            root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.AreEqual(404, root.GetProperty("status").GetInt32());
        Assert.AreEqual("Not Found", root.GetProperty("title").GetString());
        Assert.AreEqual("https://tools.ietf.org/html/rfc9110#section-15.5.5", root.GetProperty("type").GetString());
        StringAssert.Matches(root.GetProperty("traceId").GetString(), new Regex(EnrollApi.TraceParent));
    }

    public static async Task<Enrollment> EnrollmentAsync(TestApp app, long id)
    {
        await using var context = app.CreateContext();

        return await context.Enrollments.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    public static async Task<string> SnapshotAsync(TestApp app, long id)
    {
        var x = await EnrollmentAsync(app, id);

        return $"{x.Id}|{x.UserId}|{x.ClassId}|{x.Seats}|{x.Status}|{x.CreatedAt:O}|{x.CancelledAt:O}|{x.RefundedVisits}";
    }

    public static async Task AssertNoCancelTraceAsync(TestApp app, long id, TestUser user, int remainingVisits)
    {
        var enrollment = await EnrollmentAsync(app, id);

        Assert.AreEqual("active", enrollment.Status);
        Assert.IsNull(enrollment.CancelledAt);
        Assert.IsNull(enrollment.RefundedVisits);
        Assert.AreEqual(remainingVisits, await EnrollApi.RemainingVisitsAsync(app, user));
    }
}
