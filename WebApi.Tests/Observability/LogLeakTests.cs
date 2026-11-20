using System.Net;
using System.Net.Http.Headers;
using DataAccessLayer.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;
using WebApi.Tests.Waitlist;

namespace WebApi.Tests.Observability;

[TestClass]
public class LogLeakTests
{
    [TestMethod]
    public async Task LoginAndAuthorizedRequest_AllCategoriesAtTrace_LogsContainNoEmailPasswordOrToken()
    {
        await using var app = await TestApp.StartAsync(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Default"] = "Trace",
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                    ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Trace"
                })));
        using var client = app.CreateClient();

        var token = await client.GetTokenAsync(AuthClient.SeededUser);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync("/harness/me");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var records = app.Logs.GetSnapshot();

        Assert.IsTrue(
            records.Any(x => x.Category == "Microsoft.EntityFrameworkCore.Database.Command"),
            "EF command log was not collected.");
        Assert.IsTrue(
            records.Any(x => x.Category == "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler"),
            "JwtBearer log was not collected.");

        var secrets = new[] { AuthClient.SeededUser, DatabaseSeeder.DefaultPassword, token };
        var leaks = records
            .SelectMany(record => Texts(record).Select(text => (record.Category, text)))
            .Where(x => secrets.Any(secret => x.text.Contains(secret, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Category}: {x.text}")
            .ToList();

        Assert.AreEqual(0, leaks.Count, string.Join(Environment.NewLine, leaks));
    }

    [TestMethod]
    public async Task JoinWaitlist_CreatedReplayedAndRejected_LogsContainNeitherEmailNorIdempotencyKey()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await WaitlistApi.FullClassAsync(app);
        using var client = await app.CreateClientAsync(user.Email);
        var json = $"{{\"classId\":{classId},\"seats\":1}}";
        var key = $"created-{Guid.NewGuid():N}";
        var rejectedKey = $"rejected-{Guid.NewGuid():N}";

        using var created = await WaitlistApi.PostAsync(client, json, key);
        using var replayed = await WaitlistApi.PostAsync(client, json, key);
        using var rejected = await WaitlistApi.PostAsync(client, json, rejectedKey);

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Created, replayed.StatusCode, await replayed.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
        var records = app.Logs.GetSnapshot();
        Assert.AreEqual(1, records.Count(x => x.Id.Id == 3001));
        Assert.AreEqual(1, records.Count(x => x.Id.Id == 1004));
        Assert.AreEqual(1, records.Count(x => x.Id.Id == 1001));
        var secrets = new[] { user.Email, key, rejectedKey };
        var leaks = records
            .SelectMany(record => Texts(record).Select(text => (record.Category, text)))
            .Where(x => secrets.Any(secret => x.text.Contains(secret, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Category}: {x.text}")
            .ToList();

        Assert.AreEqual(0, leaks.Count, string.Join(Environment.NewLine, leaks));
    }

    private static IEnumerable<string> Texts(FakeLogRecord record)
    {
        yield return record.Message;

        if (record.Exception is not null)
        {
            yield return record.Exception.ToString();
        }

        foreach (var pair in record.StructuredState ?? [])
        {
            yield return $"{pair.Key}={pair.Value}";
        }

        foreach (var scope in record.Scopes)
        {
            yield return scope?.ToString() ?? string.Empty;

            if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    yield return $"{pair.Key}={pair.Value}";
                }
            }
        }
    }
}
