using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Common.Caching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using WebApi.Auth;
using WebApi.Tests.Contract;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Harness;

[TestClass]
public class TestAppTests
{
    [TestMethod]
    public async Task StartAsync_Clock_IsTimeProviderOfHost()
    {
        await using var app = await TestApp.StartAsync();

        Assert.AreSame(app.Clock, app.Services.GetService<TimeProvider>());
    }

    [TestMethod]
    public async Task StartAsync_Clock_MatchesSeederNow()
    {
        await using var app = await TestApp.StartAsync();
        var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        await using var context = app.CreateContext();

        var offsets = await context.Classes
            .Where(x => x.StartsAt > now && x.StartsAt <= now.AddHours(2))
            .OrderBy(x => x.StartsAt)
            .Select(x => x.StartsAt)
            .ToListAsync();

        CollectionAssert.AreEqual(
            new[] { 20d, 50d, 80d, 100d, 115d },
            offsets.Select(x => (x - now).TotalMinutes).ToArray());
    }

    [TestMethod]
    public async Task StartAsync_CacheService_WritesUnderTestPrefix()
    {
        await using var app = await TestApp.StartAsync();
        var key = $"harness:{Guid.NewGuid():N}";

        await app.Services.GetRequiredService<ICacheService>().SetAsync(key, 42);

        await using var raw = await ConnectionMultiplexer.ConnectAsync(TestDatabase.RedisConnectionString);
        var database = raw.GetDatabase();

        Assert.AreEqual("42", (string?)await database.StringGetAsync(app.RedisKeyPrefix + key));
        Assert.IsFalse(await database.KeyExistsAsync(key));
        Assert.AreEqual("42", (string?)await app.Redis.StringGetAsync(key));
    }

    [TestMethod]
    public async Task CreateClientAsync_SeededUser_AuthenticatedAsThatUser()
    {
        await using var app = await TestApp.StartAsync();
        var userId = await GetUserIdAsync(app, AuthClient.SeededUser);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.GetAsync("/harness/me");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(userId, await response.Content.ReadFromJsonAsync<long>());
    }

    [TestMethod]
    public async Task CreateClientAsync_ClockAdvancedPastTokenLifetime_TokenAccepted()
    {
        await using var app = await TestApp.StartAsync();
        var lifetime = TimeSpan.FromMinutes(app.Services.GetRequiredService<JwtOptions>().LifetimeMinutes);
        var userId = await GetUserIdAsync(app, AuthClient.SeededUser);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        app.Clock.Advance(lifetime + TimeSpan.FromDays(1));

        using var response = await client.GetAsync("/harness/me");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(userId, await response.Content.ReadFromJsonAsync<long>());
    }

    [TestMethod]
    public async Task HarnessMe_TokenExpiredByRealTime_Returns401()
    {
        await using var app = await TestApp.StartAsync();
        var userId = await GetUserIdAsync(app, AuthClient.SeededUser);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(app.Services.GetRequiredService<JwtOptions>(), userId, DateTime.UtcNow.AddMinutes(-1)));

        using var response = await client.GetAsync("/harness/me");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task StartAsync_Logs_CollectsLogsOfHost()
    {
        await using var app = await TestApp.StartAsync();
        var marker = Guid.NewGuid().ToString("N");

        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Harness")
            .LogInformation("Marker {Marker}", marker);

        var records = app.Logs.GetSnapshot().Where(x => x.Category == "Harness").ToList();

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual(LogLevel.Information, records[0].Level);
        Assert.AreEqual(marker, records[0].GetStructuredStateValue("Marker"));
    }

    [TestMethod]
    public async Task StartAsync_Metrics_CollectsCountersOfHostFromStart()
    {
        await using var app = await TestApp.StartAsync();
        await using var other = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.PostAsync("/probe/reject", null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var measurements = app.Metrics("club.requests.rejected").GetMeasurementSnapshot();
        Assert.AreEqual(1, measurements.Count);
        var measurement = measurements[0];
        Assert.AreEqual(1, measurement.Value);
        CollectionAssert.AreEquivalent(
            new Dictionary<string, object?>
            {
                ["operation"] = ContractProbeController.Operation,
                ["reason"] = ContractProbeController.Reason
            },
            measurement.Tags.ToDictionary(x => x.Key, x => x.Value));
        Assert.AreEqual(0, other.Metrics("club.requests.rejected").GetMeasurementSnapshot().Count);
        Assert.AreEqual(0, app.Metrics("club.enrollments.created").GetMeasurementSnapshot().Count);
        Assert.AreEqual(0, app.Metrics("club.visits.charged").GetMeasurementSnapshot().Count);
    }

    [TestMethod]
    public async Task Metrics_UnknownInstrument_Throws()
    {
        await using var app = await TestApp.StartAsync();

        var exception = Assert.ThrowsException<ArgumentException>(() => app.Metrics("club.unknown"));

        StringAssert.Contains(exception.Message, "club.unknown");
    }

    [TestMethod]
    public async Task StartAsync_Configure_AppliedToHost()
    {
        var marker = new HarnessMarker();

        await using var app = await TestApp.StartAsync(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton(marker)));

        Assert.AreSame(marker, app.Services.GetService<HarnessMarker>());
    }

    [TestMethod]
    public async Task DisposeAsync_App_DropsItsClone()
    {
        var app = await TestApp.StartAsync();
        var name = TestDbTests.DatabaseName(app.ConnectionString);

        Assert.IsTrue(await TestDbTests.DatabaseExistsAsync(name));

        await app.DisposeAsync();

        Assert.IsFalse(await TestDbTests.DatabaseExistsAsync(name));
    }

    private static async Task<long> GetUserIdAsync(TestApp app, string email)
    {
        await using var context = app.CreateContext();

        return await context.Users.Where(x => x.Email == email).Select(x => x.Id).SingleAsync();
    }

    private static string CreateToken(JwtOptions options, long userId, DateTime expiresAt)
    {
        var token = new JwtSecurityToken(
            options.Issuer,
            options.Audience,
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, "user")],
            expiresAt.AddHours(-1),
            expiresAt,
            new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class HarnessMarker;
}
