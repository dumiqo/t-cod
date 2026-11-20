using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebApi.Tests.Analytics;

public static class RoomLoadApi
{
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string query)
    {
        return client.GetAsync($"/analytics/load?{query}");
    }

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, DateTime from, DateTime to, string extra = "")
    {
        return GetAsync(client, $"from={Date(from)}&to={Date(to)}{extra}");
    }

    public static string Date(DateTime value)
    {
        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static async Task AssertItemsAsync(HttpResponseMessage response, params object[] items)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        var expected = JsonSerializer.Serialize(new { items });
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(text)), $"expected {expected}, got {text}");
    }
}
