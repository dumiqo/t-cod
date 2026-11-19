using System.Net;
using System.Text.Json;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Contract;

[TestClass]
public class QueryBindingTests
{
    [TestMethod]
    [DataRow("2026-09-22T13:00+03:00")]
    [DataRow("2026-09-22T10:00:00Z")]
    [DataRow("2026-09-22T10:00")]
    public async Task GetMoment_ValidValue_BindsUtcMoment(string at)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.GetAsync($"/probe/moment?at={Uri.EscapeDataString(at)}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("\"2026-09-22T10:00:00Z\"", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("?at=")]
    public async Task GetMoment_Missing_Returns400Required(string query)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.GetAsync($"/probe/moment{query}");

        await AssertSingleErrorAsync(response, "at", "The at field is required.");
    }

    [TestMethod]
    public async Task GetMoment_SpaceInsteadOfT_Returns400NotValid()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.GetAsync("/probe/moment?at=2026-09-22%2010:00");

        await AssertSingleErrorAsync(response, "at", "The value '2026-09-22 10:00' is not valid for at.");
    }

    [TestMethod]
    public async Task GetPeriod_ValidDates_BindsDates()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.GetAsync("/probe/period?from=2026-09-22&to=2026-09-23");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(
            "{\"from\":\"2026-09-22\",\"to\":\"2026-09-23\"}",
            await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task GetPeriod_DateWithTime_Returns400NotValid()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.GetAsync("/probe/period?from=2026-09-22T10:00&to=2026-09-23");

        await AssertSingleErrorAsync(response, "from", "The value '2026-09-22T10:00' is not valid for from.");
    }

    [TestMethod]
    public async Task GetPeriod_MissingTo_Returns400Required()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.GetAsync("/probe/period?from=2026-09-22");

        await AssertSingleErrorAsync(response, "to", "The to field is required.");
    }

    private static async Task AssertSingleErrorAsync(HttpResponseMessage response, string key, string message)
    {
        var text = await response.Content.ReadAsStringAsync();

        await InputRulesTests.AssertValidationProblemAsync(response, key);

        using var body = JsonDocument.Parse(text);
        var messages = body.RootElement.GetProperty("errors").GetProperty(key)
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();

        CollectionAssert.AreEqual(new[] { message }, messages, text);
    }
}
