using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Contract;

[TestClass]
public class InputRulesTests
{
    [TestMethod]
    [DataRow("abc")]
    [DataRow("9223372036854775808")]
    public async Task GetItem_IdNotLong_Returns400WithIdError(string id)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.GetAsync($"/probe/items/{id}");

        await AssertValidationProblemAsync(response, "id");
    }

    [TestMethod]
    public async Task PostBody_NoBodyNoContentType_Returns400WithRequiredBodyError()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.PostAsync("/probe/body", null);

        await AssertValidationProblemAsync(response, string.Empty);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("null")]
    public async Task PostBody_EmptyOrNullJson_Returns400WithRequiredBodyError(string json)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.PostAsync("/probe/body", Json(json));

        await AssertValidationProblemAsync(response, string.Empty);
    }

    [TestMethod]
    public async Task PostBody_TextPlainContent_Returns415()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.PostAsync(
            "/probe/body",
            new StringContent("{\"seats\":2}", Encoding.UTF8, "text/plain"));

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [TestMethod]
    public async Task PostBody_QuotedNumber_Returns400WithSeatsError()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.PostAsync("/probe/body", Json("{\"seats\":\"2\"}"));

        await AssertValidationProblemAsync(response, "$.seats", "body");
    }

    [TestMethod]
    public async Task PostBody_MissingSeats_Returns400WithSeatsError()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.PostAsync("/probe/body", Json("{}"));

        await AssertValidationProblemAsync(response, "Seats");
    }

    [TestMethod]
    public async Task PostBody_ValidBody_Returns200WithSeats()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.PostAsync("/probe/body", Json("{\"seats\":2}"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("2", await response.Content.ReadAsStringAsync());
    }

    private static StringContent Json(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return content;
    }

    internal static async Task AssertValidationProblemAsync(HttpResponseMessage response, params string[] errorKeys)
    {
        var text = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, text);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        var keys = root.GetProperty("errors").EnumerateObject().Select(x => x.Name).ToArray();

        Assert.AreEqual(400, root.GetProperty("status").GetInt32());
        Assert.IsTrue(root.TryGetProperty("traceId", out _), text);
        CollectionAssert.AreEquivalent(errorKeys, keys, text);
    }
}
