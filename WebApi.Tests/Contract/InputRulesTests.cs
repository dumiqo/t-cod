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
    public async Task PostBody_NullSeats_Returns400WithSeatsError()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.PostAsync("/probe/body", Json("{\"seats\":null}"));

        await AssertValidationProblemAsync(response, "Seats");
    }

    [TestMethod]
    [DataRow("{\"seats\":2.5}")]
    [DataRow("{\"seats\":2147483648}")]
    [DataRow("{\"seats\":true}")]
    public async Task PostBody_SeatsNotInt_Returns400WithSeatsError(string json)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.PostAsync("/probe/body", Json(json));

        await AssertValidationProblemAsync(response, "$.seats", "body");
    }

    [TestMethod]
    [DataRow("{", "$")]
    [DataRow("{\"seats\":}", "$.seats")]
    [DataRow("{\"seats\":2", "$.seats")]
    [DataRow("not json", "$")]
    public async Task PostBody_MalformedJson_Returns400WithPathError(string json, string path)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.PostAsync("/probe/body", Json(json));

        await AssertValidationProblemAsync(response, path, "body");
    }

    [TestMethod]
    public async Task PostBody_EmptyChunkedBodyNoContentType_Returns415()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/probe/body")
        {
            Content = new ByteArrayContent([])
        };
        request.Headers.TransferEncodingChunked = true;

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [TestMethod]
    [DataRow("5", "5")]
    [DataRow("-1", "-1")]
    [DataRow("9223372036854775807", "9223372036854775807")]
    public async Task GetItem_IdLong_Returns200WithId(string id, string expected)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await client.GetAsync($"/probe/items/{id}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(expected, await response.Content.ReadAsStringAsync());
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
