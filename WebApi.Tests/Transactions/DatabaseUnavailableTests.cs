using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Enrollments.EnrollApi;

namespace WebApi.Tests.Transactions;

[TestClass]
[DoNotParallelize]
public class DatabaseUnavailableTests
{
    [TestMethod]
    public async Task Enroll_PoolExhausted_Returns503DatabaseUnavailableAndRecovers()
    {
        string? poolLimited = null;
        await using var app = await TestApp.StartAsync(builder => builder.ConfigureAppConfiguration((_, configuration) =>
        {
            poolLimited = configuration.Build().GetConnectionString("Postgres") + ";Maximum Pool Size=2;Timeout=1";
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = poolLimited });
        }));
        var user = await app.Data.UserAsync(remainingVisits: 10);
        var classId = await app.Data.ClassAsync();
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";
        var first = await HeldLock.AcquireAsync(poolLimited!, "SELECT 1");
        var second = await HeldLock.AcquireAsync(poolLimited!, "SELECT 1");

        using (var response = await PostAsync(client, json, "key-1"))
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, text);
            Assert.AreEqual("1", response.Headers.RetryAfter?.ToString());
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(text);
            Assert.AreEqual(503, body.RootElement.GetProperty("status").GetInt32());
            Assert.AreEqual("database-unavailable", body.RootElement.GetProperty("reason").GetString());
        }

        var unavailable = app.Logs.GetSnapshot().Single(x => x.Id.Id == 1006);
        Assert.AreEqual("DatabaseUnavailable", unavailable.Id.Name);
        Assert.AreEqual(LogLevel.Warning, unavailable.Level);
        Assert.AreEqual("Enroll", unavailable.GetStructuredStateValue("Operation"));
        Assert.AreEqual(user.Id.ToString(), unavailable.GetStructuredStateValue("UserId"));
        StringAssert.Contains(unavailable.Exception?.ToString(), "System.TimeoutException");
        var rejected = app.Metrics("club.requests.rejected").GetMeasurementSnapshot().Single();
        Assert.AreEqual(1, rejected.Value);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, object?> { ["operation"] = "Enroll", ["reason"] = "database-unavailable" },
            rejected.Tags.ToDictionary(x => x.Key, x => x.Value));

        await first.DisposeAsync();
        await second.DisposeAsync();
        await AssertNoTraceAsync(app, user, 10);

        using var repeat = await PostAsync(client, json, "key-1");

        Assert.AreEqual(HttpStatusCode.Created, repeat.StatusCode, await repeat.Content.ReadAsStringAsync());
        Assert.AreEqual(9, await RemainingVisitsAsync(app, user));
        Assert.AreEqual(1, CreatedEvents(app).Count);
        Assert.AreEqual(1, app.Logs.GetSnapshot().Count(x => x.Id.Id == 1006));
    }

    [TestMethod]
    public async Task Login_PoolExhausted_Returns503WithoutUserId()
    {
        string? poolLimited = null;
        await using var app = await TestApp.StartAsync(builder => builder.ConfigureAppConfiguration((_, configuration) =>
        {
            poolLimited = configuration.Build().GetConnectionString("Postgres") + ";Maximum Pool Size=1;Timeout=1";
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = poolLimited });
        }));
        using var client = app.CreateClient();
        await using var held = await HeldLock.AcquireAsync(poolLimited!, "SELECT 1");

        using var response = await client.PostAsync(
            "/auth/login",
            new StringContent("{\"email\":\"user001@club.test\",\"password\":\"Password123!\"}", System.Text.Encoding.UTF8, "application/json"));

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, text);
        Assert.AreEqual("1", response.Headers.RetryAfter?.ToString());
        using var body = JsonDocument.Parse(text);
        Assert.AreEqual("database-unavailable", body.RootElement.GetProperty("reason").GetString());
        var unavailable = app.Logs.GetSnapshot().Single(x => x.Id.Id == 1006);
        Assert.AreEqual("Login", unavailable.GetStructuredStateValue("Operation"));
        Assert.IsNull(unavailable.GetStructuredStateValue("UserId"));
    }
}
