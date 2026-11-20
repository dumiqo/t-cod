using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebApi.Tests.Schedule;

public static class ScheduleApi
{
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string query)
    {
        return client.GetAsync($"/schedule?{query}");
    }

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, DateTime from, DateTime to, string extra = "")
    {
        return GetAsync(client, $"from={Iso(from)}&to={Iso(to)}{extra}");
    }

    public static string Iso(DateTime value)
    {
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    public static object Item(
        long classId,
        DateTime startsAt,
        long roomId,
        string roomName,
        string title = "Test class",
        int durationMinutes = 60,
        int capacity = 10)
    {
        return new
        {
            classId,
            title,
            startsAt,
            durationMinutes,
            room = new { id = roomId, name = roomName },
            capacity,
            freeSeats = capacity,
            mySeats = 0
        };
    }

    public static async Task AssertPageAsync(HttpResponseMessage response, params object[] items)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        var expected = JsonSerializer.Serialize(new { items, continuationToken = (string?)null });
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(text)), $"expected {expected}, got {text}");
    }

    public static async Task<long[]> ClassIdsAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        using var body = JsonDocument.Parse(text);

        return body.RootElement.GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("classId").GetInt64())
            .ToArray();
    }
}
