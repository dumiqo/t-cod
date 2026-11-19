using System.Net;
using System.Net.Http.Headers;
using DataAccessLayer.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Testing;
using WebApi.Tests.Infrastructure;

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
