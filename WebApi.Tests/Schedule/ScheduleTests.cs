using System.Net;
using System.Text.Json;
using WebApi.Tests.Contract;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Schedule.ScheduleApi;

namespace WebApi.Tests.Schedule;

[TestClass]
public class ScheduleTests
{
    [TestMethod]
    public async Task Get_ClassesInTwoRooms_Returns200WithExactItemsOrderedByStartsAt()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        var pool = await app.Data.RoomAsync("Pool");
        var late = await app.Data.ClassAsync(startsAt: day.AddHours(18), roomId: hall, title: "Yoga", durationMinutes: 90, capacity: 12);
        var early = await app.Data.ClassAsync(startsAt: day.AddHours(8), roomId: pool, title: "Swim", durationMinutes: 45, capacity: 6);
        var noon = await app.Data.ClassAsync(startsAt: day.AddHours(12).AddTicks(1234560), roomId: hall, title: "Box", capacity: 1);
        await app.Data.ClassAsync(startsAt: day.AddDays(1), roomId: hall);
        await app.Data.ClassAsync(startsAt: day.AddTicks(-10), roomId: pool);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, day, day.AddDays(1));

        await AssertPageAsync(
            response,
            Item(early, day.AddHours(8), pool, "Pool", "Swim", 45, 6),
            Item(noon, day.AddHours(12).AddTicks(1234560), hall, "Hall", "Box", capacity: 1),
            Item(late, day.AddHours(18), hall, "Hall", "Yoga", 90, 12));
    }

    [TestMethod]
    public async Task Get_RoomFilter_ReturnsOnlyThatRoomClasses()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        var pool = await app.Data.RoomAsync("Pool");
        var first = await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: hall);
        await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: pool);
        var second = await app.Data.ClassAsync(startsAt: day.AddHours(11), roomId: hall);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, day, day.AddDays(1), $"&roomId={hall}");

        await AssertPageAsync(
            response,
            Item(first, day.AddHours(9), hall, "Hall"),
            Item(second, day.AddHours(11), hall, "Hall"));
    }

    [TestMethod]
    public async Task Get_NoLimit_Returns20Of21()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var ids = await ClassesAsync(app, day, 21);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, day, day.AddDays(1));

        CollectionAssert.AreEqual(ids.Take(20).ToArray(), await ClassIdsAsync(response));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(100)]
    public async Task Get_Limit_ReturnsThatManyFirstClasses(int limit)
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var ids = await ClassesAsync(app, day, 101);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, day, day.AddDays(1), $"&limit={limit}");

        CollectionAssert.AreEqual(ids.Take(limit).ToArray(), await ClassIdsAsync(response));
    }

    [TestMethod]
    [DataRow("to=2027-01-02", "from", "The from field is required.")]
    [DataRow("from=2027-01-01", "to", "The to field is required.")]
    [DataRow("from=&to=2027-01-02", "from", "The from field is required.")]
    [DataRow("from=2027-01-01%2010:00&to=2027-01-02", "from", "The value '2027-01-01 10:00' is not valid for from.")]
    [DataRow("from=2027-01-01&to=2027-13-01T00:00", "to", "The value '2027-13-01T00:00' is not valid for to.")]
    [DataRow("from=2027-01-01&to=02.01.2027", "to", "The value '02.01.2027' is not valid for to.")]
    [DataRow("from=2027-01-01&to=2027-01-02&roomId=abc", "roomId", "The value 'abc' is not valid.")]
    [DataRow("from=2027-01-01&to=2027-01-02&roomId=9223372036854775808", "roomId", "The value '9223372036854775808' is not valid.")]
    [DataRow("from=2027-01-01&to=2027-01-02&limit=0", "limit", "The field limit must be between 1 and 100.")]
    [DataRow("from=2027-01-01&to=2027-01-02&limit=101", "limit", "The field limit must be between 1 and 100.")]
    [DataRow("from=2027-01-01&to=2027-01-02&limit=abc", "limit", "The value 'abc' is not valid.")]
    [DataRow("from=2027-01-01T10:00&to=2027-01-01T10:00:00Z", "to", "The to field must be later than from.")]
    [DataRow("from=2027-01-01T10:00:00.0000001Z&to=2027-01-01T10:00Z", "to", "The to field must be later than from.")]
    [DataRow("from=2027-01-02&to=2027-01-01", "to", "The to field must be later than from.")]
    public async Task Get_InvalidQuery_Returns400WithError(string query, string key, string message)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, query);

        await QueryBindingTests.AssertSingleErrorAsync(response, key, message);
    }

    [TestMethod]
    [DataRow("from=bad&to=&roomId=abc&limit=0", new[] { "from", "limit", "roomId", "to" })]
    [DataRow("from=2027-01-02&to=2027-01-01&limit=101", new[] { "limit", "to" })]
    public async Task Get_SeveralInvalidParameters_ReturnsAllErrorsInOne400(string query, string[] keys)
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, query);

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, text);
        using var body = JsonDocument.Parse(text);
        var actual = body.RootElement.GetProperty("errors").EnumerateObject().Select(x => x.Name).Order().ToArray();
        CollectionAssert.AreEqual(keys, actual, text);
    }

    [TestMethod]
    public async Task Get_NoToken_Returns401()
    {
        await using var app = await TestApp.StartAsync();
        using var client = app.CreateClient();

        using var response = await GetAsync(client, "from=2027-01-01&to=2027-01-02");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Get_CancelledClassInPeriod_IsNotReturned()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: hall, cancelled: true);
        var active = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: hall);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, day, day.AddDays(1));

        await AssertPageAsync(response, Item(active, day.AddHours(10), hall, "Hall"));
    }

    [TestMethod]
    public async Task Get_ClassesOnBoundaries_FromIncludedToExcluded()
    {
        await using var app = await TestApp.StartAsync();
        var from = FarDay(app).AddHours(10);
        var to = from.AddHours(2);
        var hall = await app.Data.RoomAsync("Hall");
        await app.Data.ClassAsync(startsAt: from.AddTicks(-10), roomId: hall);
        var atFrom = await app.Data.ClassAsync(startsAt: from, roomId: hall);
        var beforeTo = await app.Data.ClassAsync(startsAt: to.AddTicks(-10), roomId: hall);
        await app.Data.ClassAsync(startsAt: to, roomId: hall);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, from, to);

        await AssertPageAsync(
            response,
            Item(atFrom, from, hall, "Hall"),
            Item(beforeTo, to.AddTicks(-10), hall, "Hall"));
    }

    [TestMethod]
    public async Task Get_FromOneTickAfterClass_ExcludesIt()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = FarDay(app).AddHours(10);
        await app.Data.ClassAsync(startsAt: startsAt, roomId: await app.Data.RoomAsync("Hall"));
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, startsAt.AddTicks(1), startsAt.AddHours(1));

        await AssertPageAsync(response);
    }

    [TestMethod]
    public async Task Get_ToOneTickAfterClass_IncludesIt()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = FarDay(app).AddHours(10);
        var hall = await app.Data.RoomAsync("Hall");
        var id = await app.Data.ClassAsync(startsAt: startsAt, roomId: hall);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, startsAt.AddHours(-1), startsAt.AddTicks(1));

        await AssertPageAsync(response, Item(id, startsAt, hall, "Hall"));
    }

    [TestMethod]
    public async Task Get_FromWithOffset_ConvertsToUtc()
    {
        await using var app = await TestApp.StartAsync();
        var from = FarDay(app).AddHours(10);
        var hall = await app.Data.RoomAsync("Hall");
        await app.Data.ClassAsync(startsAt: from.AddMinutes(-1), roomId: hall);
        var id = await app.Data.ClassAsync(startsAt: from, roomId: hall);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        var local = from.AddHours(3).ToString("yyyy-MM-dd'T'HH:mm") + "%2B03:00";

        using var response = await GetAsync(client, $"from={local}&to={Iso(from.AddHours(1))}");

        await AssertPageAsync(response, Item(id, from, hall, "Hall"));
    }

    [TestMethod]
    public async Task Get_FromAndToWithinOneMicrosecond_Returns200Empty()
    {
        await using var app = await TestApp.StartAsync();
        var second = FarDay(app).AddHours(10);
        await app.Data.ClassAsync(startsAt: second, roomId: await app.Data.RoomAsync("Hall"));
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, second.AddTicks(1), second.AddTicks(2));

        await AssertPageAsync(response);
    }

    [TestMethod]
    public async Task Get_ToMaxValue_ReturnsClassesOfPeriod()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        var id = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: hall);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, $"from={Iso(day)}&to=9999-12-31T23:59:59.9999999Z");

        await AssertPageAsync(response, Item(id, day.AddHours(10), hall, "Hall"));
    }

    [TestMethod]
    public async Task Get_EmptyRoomIdAndLimit_SameAsAbsent()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        var pool = await app.Data.RoomAsync("Pool");
        var first = await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: hall);
        var second = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: pool);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, day, day.AddDays(1), "&roomId=&limit=");

        await AssertPageAsync(
            response,
            Item(first, day.AddHours(9), hall, "Hall"),
            Item(second, day.AddHours(10), pool, "Pool"));
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("999999999")]
    public async Task Get_MissingRoom_ReturnsEmptyPage(string roomId)
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: await app.Data.RoomAsync("Hall"));
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, day, day.AddDays(1), $"&roomId={roomId}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("{\"items\":[],\"continuationToken\":null}", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Get_SameStartsAt_OrderedById()
    {
        await using var app = await TestApp.StartAsync();
        var startsAt = FarDay(app).AddHours(10);
        var hall = await app.Data.RoomAsync("Hall");
        var pool = await app.Data.RoomAsync("Pool");
        var first = await app.Data.ClassAsync(startsAt: startsAt, roomId: pool);
        var second = await app.Data.ClassAsync(startsAt: startsAt, roomId: hall);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, startsAt, startsAt.AddHours(1));

        await AssertPageAsync(
            response,
            Item(first, startsAt, pool, "Pool"),
            Item(second, startsAt, hall, "Hall"));
    }

    private static DateTime FarDay(TestApp app)
    {
        return app.SeededAt.UtcDateTime.Date.AddDays(400);
    }

    private static async Task<long[]> ClassesAsync(TestApp app, DateTime day, int count)
    {
        var room = await app.Data.RoomAsync("Hall");
        var ids = new long[count];

        for (var i = 0; i < count; i++)
        {
            ids[i] = await app.Data.ClassAsync(startsAt: day.AddMinutes(count - i), roomId: room);
        }

        return ids.Reverse().ToArray();
    }
}
