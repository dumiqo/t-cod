using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataAccessLayer.Transactions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Transactions;

[TestClass]
public class TransactionConflictTests
{
    private const string TraceParent = "^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$";

    [TestMethod]
    public async Task LockTimeout_Returns503WithRetryAfterAndLeavesNoTrace()
    {
        await using var app = await StartAsync("00:00:00.300");
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        var (classId, passId, visits) = await SeededDataAsync(app);
        await using var held = await HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR UPDATE");

        using var response = await client.PostAsync($"/probe/transactions/spend-and-lock/{classId}", null);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual("1", response.Headers.GetValues("Retry-After").Single());
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        var fields = root.EnumerateObject().Select(x => x.Name).Order().ToArray();

        CollectionAssert.AreEqual(new[] { "reason", "status", "title", "traceId", "type" }, fields);
        Assert.AreEqual(503, root.GetProperty("status").GetInt32());
        Assert.AreEqual("Service Unavailable", root.GetProperty("title").GetString());
        Assert.AreEqual("https://tools.ietf.org/html/rfc9110#section-15.6.4", root.GetProperty("type").GetString());
        Assert.AreEqual("lock-timeout", root.GetProperty("reason").GetString());
        StringAssert.Matches(root.GetProperty("traceId").GetString(), new Regex(TraceParent));

        await using var context = app.CreateContext();

        Assert.AreEqual(visits, await context.Passes.Where(x => x.Id == passId).Select(x => x.RemainingVisits).SingleAsync());
    }

    [TestMethod]
    public async Task LockTimeout_LogsTransactionConflictEventWithResponseTraceId()
    {
        await using var app = await StartAsync("00:00:00.300");
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        var (classId, _, _) = await SeededDataAsync(app);
        var userId = await SeededUserIdAsync(app);
        await using var held = await HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR UPDATE");

        using var response = await client.PostAsync($"/probe/transactions/spend-and-lock/{classId}", null);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var traceId = body.RootElement.GetProperty("traceId").GetString()!.Split('-')[1];

        var record = app.Logs.GetSnapshot().Single(x => x.Id.Id == 1002);

        Assert.AreEqual("TransactionConflict", record.Id.Name);
        Assert.AreEqual(LogLevel.Warning, record.Level);
        Assert.AreEqual(TransactionProbeController.Operation, record.GetStructuredStateValue("Operation"));
        Assert.AreEqual("lock-timeout", record.GetStructuredStateValue("Reason"));
        Assert.AreEqual(userId.ToString(), record.GetStructuredStateValue("UserId"));
        Assert.IsInstanceOfType<TransactionConflictException>(record.Exception);
        Assert.AreEqual(traceId, ScopeValue(record, "TraceId"));
        Assert.AreEqual($"/probe/transactions/spend-and-lock/{classId}", ScopeValue(record, "RequestPath"));
        Assert.IsFalse(app.Logs.GetSnapshot().Any(x => x.Category == "WebApi.Middleware.ExceptionHandlingMiddleware"));
    }

    [TestMethod]
    public async Task LockTimeout_CountsRejectedRequestByOperationAndReason()
    {
        await using var app = await StartAsync("00:00:00.300");
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        var (classId, _, _) = await SeededDataAsync(app);
        using var collector = new MetricCollector<long>(
            app.Services.GetRequiredService<IMeterFactory>(),
            "FitnessClub.Api",
            "club.requests.rejected");
        await using var held = await HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classId} FOR UPDATE");

        using var response = await client.PostAsync($"/probe/transactions/spend-and-lock/{classId}", null);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var measurement = collector.GetMeasurementSnapshot().Single();

        Assert.AreEqual(1, measurement.Value);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, object?>
            {
                ["operation"] = TransactionProbeController.Operation,
                ["reason"] = "lock-timeout"
            },
            measurement.Tags.ToDictionary(x => x.Key, x => x.Value));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Deadlock_Returns503WithDeadlockReason()
    {
        await using var app = await StartAsync("00:00:02");
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        long[] classIds;
        await using (var context = app.CreateContext())
        {
            classIds = await context.Classes.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToArrayAsync();
        }

        await using var held = await HeldLock.AcquireAsync(app.ConnectionString, $"SELECT 1 FROM classes WHERE id = {classIds[1]} FOR UPDATE");

        var request = client.PostAsync($"/probe/transactions/lock-both/{classIds[0]}/{classIds[1]}", null);
        await held.WaitForBlockedAsync(1);
        await held.ExecuteAsync($"SELECT 1 FROM classes WHERE id = {classIds[0]} FOR UPDATE");
        using var response = await request;

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual("1", response.Headers.GetValues("Retry-After").Single());

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual("deadlock", body.RootElement.GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task UniqueViolation_Returns500WithoutConflictEvent()
    {
        await using var app = await StartAsync("00:00:00.300");
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        var userId = await SeededUserIdAsync(app);

        using var response = await client.PostAsync("/probe/transactions/duplicate-pass", null);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("Retry-After"));
        Assert.IsFalse(app.Logs.GetSnapshot().Any(x => x.Id.Id == 1002));

        await using var context = app.CreateContext();

        Assert.AreEqual(1, await context.Passes.CountAsync(x => x.UserId == userId));
    }

    private static Task<TestApp> StartAsync(string lockTimeout)
    {
        return TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", lockTimeout));
    }

    private static async Task<long> SeededUserIdAsync(TestApp app)
    {
        await using var context = app.CreateContext();

        return await context.Users.Where(x => x.Email == AuthClient.SeededUser).Select(x => x.Id).SingleAsync();
    }

    private static async Task<(long ClassId, long PassId, int Visits)> SeededDataAsync(TestApp app)
    {
        var userId = await SeededUserIdAsync(app);
        await using var context = app.CreateContext();
        var classId = await context.Classes.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        var pass = await context.Passes.SingleAsync(x => x.UserId == userId);

        return (classId, pass.Id, pass.RemainingVisits);
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
