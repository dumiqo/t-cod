using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Contract;

[TestClass]
public class RejectTests
{
    [TestMethod]
    public async Task Reject_NoToken_Returns401()
    {
        await using var app = await TestApp.StartAsync();
        using var client = app.CreateClient();

        using var response = await client.PostAsync("/probe/reject", null);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Reject_Returns409ProblemDetailsWithReason()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.PostAsync("/probe/reject", null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        var fields = root.EnumerateObject().Select(x => x.Name).Order().ToArray();

        CollectionAssert.AreEqual(new[] { "reason", "status", "title", "traceId", "type" }, fields);
        Assert.AreEqual(409, root.GetProperty("status").GetInt32());
        Assert.AreEqual("Conflict", root.GetProperty("title").GetString());
        Assert.AreEqual("https://tools.ietf.org/html/rfc9110#section-15.5.10", root.GetProperty("type").GetString());
        Assert.AreEqual(ContractProbeController.Reason, root.GetProperty("reason").GetString());
        StringAssert.Matches(root.GetProperty("traceId").GetString(), new Regex(TraceParent));
    }

    [TestMethod]
    public async Task Reject_LogsRequestRejectedEventWithResponseTraceId()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        long userId;
        await using (var context = app.CreateContext())
        {
            userId = await context.Users.Where(x => x.Email == AuthClient.SeededUser).Select(x => x.Id).SingleAsync();
        }

        using var response = await client.PostAsync("/probe/reject", null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var traceId = body.RootElement.GetProperty("traceId").GetString()!.Split('-')[1];

        var record = app.Logs.GetSnapshot().Single(x => x.Id.Id == 1001);

        Assert.AreEqual("RequestRejected", record.Id.Name);
        Assert.AreEqual(LogLevel.Information, record.Level);
        Assert.AreEqual(ContractProbeController.Operation, record.GetStructuredStateValue("Operation"));
        Assert.AreEqual(ContractProbeController.Reason, record.GetStructuredStateValue("Reason"));
        Assert.AreEqual(userId.ToString(), record.GetStructuredStateValue("UserId"));
        Assert.AreEqual(traceId, ScopeValue(record, "TraceId"));
        Assert.AreEqual("/probe/reject", ScopeValue(record, "RequestPath"));
    }

    [TestMethod]
    public async Task Reject_CountsRejectedRequestByOperationAndReason()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        using var collector = new MetricCollector<long>(
            app.Services.GetRequiredService<IMeterFactory>(),
            "FitnessClub.Api",
            "club.requests.rejected");

        using var response = await client.PostAsync("/probe/reject", null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);

        var measurement = collector.GetMeasurementSnapshot().Single();

        Assert.AreEqual(1, measurement.Value);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, object?>
            {
                ["operation"] = ContractProbeController.Operation,
                ["reason"] = ContractProbeController.Reason
            },
            measurement.Tags.ToDictionary(x => x.Key, x => x.Value));
    }

    private const string TraceParent = "^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$";

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
