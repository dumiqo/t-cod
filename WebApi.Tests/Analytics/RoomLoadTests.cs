using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WebApi.Tests.Contract;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Analytics.RoomLoadApi;

namespace WebApi.Tests.Analytics;

[TestClass]
public class RoomLoadTests
{
    [TestMethod]
    public async Task Get_AdminPeriodWithoutClasses_Returns200WithEmptyItems()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day.AddDays(30));

        await AssertItemsAsync(response);
    }

    [TestMethod]
    public async Task Get_NoToken_Returns401WithEmptyBody()
    {
        await using var app = await TestApp.StartAsync();
        using var client = app.CreateClient();

        using var response = await GetAsync(client, "from=2027-01-01&to=2027-01-02");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Get_InvalidToken_Returns401WithEmptyBody()
    {
        await using var app = await TestApp.StartAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        using var response = await GetAsync(client, "from=2027-01-01&to=2027-01-02");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("from=2027-01-01&to=2027-01-02")]
    [DataRow("from=2027-01-02&to=2027-01-01")]
    [DataRow("from=bad&to=2027-13-01&roomId=abc")]
    [DataRow("to=2027-01-02")]
    [DataRow("")]
    public async Task Get_NotAdmin_Returns403WithEmptyBodyBeforeValidation(string query)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, query);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("to=2027-01-02", "from", "The from field is required.")]
    [DataRow("from=2027-01-01", "to", "The to field is required.")]
    [DataRow("from=&to=2027-01-02", "from", "The from field is required.")]
    [DataRow("from=2027-01-01&to=", "to", "The to field is required.")]
    [DataRow("from=2026-09-22T00:00&to=2026-09-23", "from", "The value '2026-09-22T00:00' is not valid for from.")]
    [DataRow("from=2026-09-22T00:00Z&to=2026-09-23", "from", "The value '2026-09-22T00:00Z' is not valid for from.")]
    [DataRow("from=2026-9-22&to=2026-09-23", "from", "The value '2026-9-22' is not valid for from.")]
    [DataRow("from=2026-09-22&to=2026-02-30", "to", "The value '2026-02-30' is not valid for to.")]
    [DataRow("from=2026-09-22&to=22.09.2026", "to", "The value '22.09.2026' is not valid for to.")]
    [DataRow("from=2027-02-28&to=2027-02-29", "to", "The value '2027-02-29' is not valid for to.")]
    [DataRow("from=2027-01-01&to=2027-01-02&roomId=abc", "roomId", "The value 'abc' is not valid.")]
    [DataRow("from=2027-01-01&to=2027-01-02&roomId=9223372036854775808", "roomId", "The value '9223372036854775808' is not valid.")]
    [DataRow("from=2027-01-02&to=2027-01-01", "to", "The to field must not be earlier than from.")]
    public async Task Get_InvalidQuery_Returns400WithError(string query, string key, string message)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, query);

        await QueryBindingTests.AssertSingleErrorAsync(response, key, message);
    }

    [TestMethod]
    [DataRow("from=bad&to=&roomId=abc", new[] { "from", "roomId", "to" })]
    [DataRow("from=2027-01-02&to=2027-01-01&roomId=abc", new[] { "roomId", "to" })]
    [DataRow("from=2027-01-02&to=2027-01-01T00:00", new[] { "to" })]
    public async Task Get_SeveralInvalidParameters_ReturnsAllErrorsInOne400(string query, string[] keys)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, query);

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, text);
        using var body = JsonDocument.Parse(text);
        var actual = body.RootElement.GetProperty("errors").EnumerateObject().Select(x => x.Name).Order().ToArray();
        CollectionAssert.AreEqual(keys, actual, text);
    }

    [TestMethod]
    public async Task Get_ToEqualsFrom_Returns200()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day);

        await AssertItemsAsync(response);
    }

    [TestMethod]
    [DataRow("from=0001-01-01&to=9999-12-31&roomId=0")]
    [DataRow("from=2028-02-29&to=2028-02-29&roomId=0")]
    [DataRow("from=9999-12-31&to=9999-12-31")]
    public async Task Get_ExtremeAndLeapDates_Returns200WithEmptyItems(string query)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, query);

        await AssertItemsAsync(response);
    }

    [TestMethod]
    public async Task Get_EmptyRoomId_TreatedAsAbsent()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day, "&roomId=");

        await AssertItemsAsync(response);
    }

    private static DateTime FarDay(TestApp app)
    {
        return app.SeededAt.UtcDateTime.Date.AddDays(400);
    }
}
