using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Contract;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Schedule.ScheduleApi;

namespace WebApi.Tests.Schedule;

[TestClass]
public class SchedulePagingTests
{
    private const string Mismatch = "The continuationToken does not match from, to and roomId.";

    [TestMethod]
    [DataRow(7)]
    [DataRow(100)]
    public async Task Get_WalkWholeSeededPeriod_ReturnsEveryClassOnceInOrder(int limit)
    {
        await using var app = await TestApp.StartAsync();
        var from = app.SeededAt.UtcDateTime.AddDays(-31);
        var to = app.SeededAt.UtcDateTime.AddDays(31);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        var actual = await WalkAsync(client, from, to, limit);

        var expected = await ExpectedIdsAsync(app, from, to);
        Assert.AreEqual(184, expected.Length);
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Get_WalkSeededSubperiodWithEqualStartsAt_ReturnsEveryClassOnceInOrder(int limit)
    {
        await using var app = await TestApp.StartAsync();
        var pairAt = app.SeededAt.UtcDateTime.AddMinutes(-21 * 432);
        var from = pairAt.AddMinutes(-20 * 432 - 216);
        var to = pairAt.AddMinutes(20 * 432 + 216);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        var actual = await WalkAsync(client, from, to, limit);

        await using var context = app.CreateContext();
        var startsAt = await context.Classes
            .Where(x => x.StartsAt >= from && x.StartsAt < to)
            .OrderBy(x => x.StartsAt)
            .ThenBy(x => x.Id)
            .Select(x => x.StartsAt)
            .ToListAsync();
        Assert.IsTrue(startsAt.Count is >= 35 and <= 45, $"{startsAt.Count} classes in the subperiod.");
        Assert.AreEqual(1, startsAt.Zip(startsAt.Skip(1)).Count(x => x.First == x.Second));
        CollectionAssert.AreEqual(await ExpectedIdsAsync(app, from, to), actual);
    }

    [TestMethod]
    public async Task Get_EqualStartsAtOnPageBoundary_SecondOfPairComesOnNextPage()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var first = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room);
        var pairFirst = await app.Data.ClassAsync(startsAt: day.AddHours(11), roomId: room);
        var pairSecond = await app.Data.ClassAsync(startsAt: day.AddHours(11), roomId: room);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var page1 = await GetAsync(client, day, day.AddDays(1), "&limit=2");
        var (ids, token) = await PageAsync(page1);
        using var page2 = await GetAsync(client, day, day.AddDays(1), $"&limit=2&continuationToken={token}");

        CollectionAssert.AreEqual(new[] { first, pairFirst }, ids);
        Assert.IsNotNull(token);
        await AssertPageAsync(page2, Item(pairSecond, day.AddHours(11), room, "Hall"));
    }

    [TestMethod]
    public async Task Get_LastPageOfExactlyLimit_HasNullToken()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var ids = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await app.Data.ClassAsync(startsAt: day.AddHours(10 + i), roomId: room));
        }

        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var page1 = await GetAsync(client, day, day.AddDays(1), "&limit=2");
        var (_, token) = await PageAsync(page1);
        using var page2 = await GetAsync(client, day, day.AddDays(1), $"&limit=2&continuationToken={token}");

        await AssertPageAsync(
            page2,
            Item(ids[2], day.AddHours(12), room, "Hall"),
            Item(ids[3], day.AddHours(13), room, "Hall"));
    }

    [TestMethod]
    public async Task Get_SinglePageOfExactlyLimit_HasNullToken()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var first = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room);
        var second = await app.Data.ClassAsync(startsAt: day.AddHours(11), roomId: room);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, day, day.AddDays(1), "&limit=2");

        await AssertPageAsync(
            response,
            Item(first, day.AddHours(10), room, "Hall"),
            Item(second, day.AddHours(11), room, "Hall"));
    }

    [TestMethod]
    public async Task Get_EmptyContinuationToken_ReturnsFirstPage()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var first = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room);
        var second = await app.Data.ClassAsync(startsAt: day.AddHours(11), roomId: room);
        await app.Data.ClassAsync(startsAt: day.AddHours(12), roomId: room);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var withEmpty = await GetAsync(client, day, day.AddDays(1), "&limit=2&continuationToken=");
        using var without = await GetAsync(client, day, day.AddDays(1), "&limit=2");

        var expected = await PageAsync(without);
        var actual = await PageAsync(withEmpty);
        CollectionAssert.AreEqual(new[] { first, second }, actual.Ids);
        Assert.AreEqual(expected.Token, actual.Token);
        Assert.IsNotNull(actual.Token);
    }

    [TestMethod]
    public async Task Get_LimitChangedBetweenPages_ContinuesAfterCursor()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await app.Data.ClassAsync(startsAt: day.AddHours(10 + i), roomId: room));
        }

        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var page1 = await GetAsync(client, day, day.AddDays(1), "&limit=2");
        var (firstIds, token) = await PageAsync(page1);
        using var page2 = await GetAsync(client, day, day.AddDays(1), $"&limit=3&continuationToken={token}");

        CollectionAssert.AreEqual(ids.Take(2).ToArray(), firstIds);
        await AssertPageAsync(
            page2,
            Item(ids[2], day.AddHours(12), room, "Hall"),
            Item(ids[3], day.AddHours(13), room, "Hall"),
            Item(ids[4], day.AddHours(14), room, "Hall"));
    }

    [TestMethod]
    [DataRow("{0:yyyy-MM-dd}", "{0:yyyy-MM-dd}T03:00+03:00")]
    [DataRow("{0:yyyy-MM-dd}T03:00+03:00", "{0:yyyy-MM-dd}T00:00:00Z")]
    [DataRow("{0:yyyy-MM-dd}T00:00:00.0000001Z", "{0:yyyy-MM-dd}T00:00:00.0000009Z")]
    [DataRow("{0:yyyy-MM-dd}T00:00:00.0000010Z", "{0:yyyy-MM-dd}T03:00:00.0000005+03:00")]
    public async Task Get_SameFromInAnotherForm_ContinuesAfterCursor(string firstFrom, string secondFrom)
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var first = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room);
        var second = await app.Data.ClassAsync(startsAt: day.AddHours(11), roomId: room);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        var to = Iso(day.AddDays(1));

        using var page1 = await GetAsync(client, $"from={Form(firstFrom, day)}&to={to}&limit=1");
        var (ids, token) = await PageAsync(page1);
        using var page2 = await GetAsync(client, $"from={Form(secondFrom, day)}&to={to}&limit=1&continuationToken={token}");

        CollectionAssert.AreEqual(new[] { first }, ids);
        await AssertPageAsync(page2, Item(second, day.AddHours(11), room, "Hall"));
    }

    [TestMethod]
    public async Task Get_UnparsableToken_Returns400()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, "from=2027-01-01&to=2027-01-02&continuationToken=abc%2B");

        await QueryBindingTests.AssertSingleErrorAsync(response, "continuationToken", "The value 'abc+' is not valid for continuationToken.");
    }

    [TestMethod]
    public async Task Get_WhitespaceToken_Returns400()
    {
        await using var app = await TestApp.StartAsync();
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, "from=2027-01-01&to=2027-01-02&continuationToken=%20%20%20");

        await QueryBindingTests.AssertSingleErrorAsync(response, "continuationToken", "The value '   ' is not valid for continuationToken.");
    }

    [TestMethod]
    [DataRow(TimeSpan.TicksPerHour, 0L, "hall", "hall")]
    [DataRow(-TimeSpan.TicksPerHour, 0L, null, null)]
    [DataRow(1L, 0L, null, null)]
    [DataRow(0L, -TimeSpan.TicksPerHour, null, null)]
    [DataRow(0L, 1L, "hall", "hall")]
    [DataRow(0L, 0L, "hall", null)]
    [DataRow(0L, 0L, null, "hall")]
    [DataRow(0L, 0L, "hall", "pool")]
    public async Task Get_TokenFromOtherParameters_Returns400(long fromShift, long toShift, string? firstRoom, string? secondRoom)
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var rooms = new Dictionary<string, long>
        {
            ["hall"] = await app.Data.RoomAsync("Hall"),
            ["pool"] = await app.Data.RoomAsync("Pool")
        };
        await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: rooms["hall"]);
        await app.Data.ClassAsync(startsAt: day.AddHours(11), roomId: rooms["hall"]);
        await app.Data.ClassAsync(startsAt: day.AddHours(12), roomId: rooms["pool"]);
        await app.Data.ClassAsync(startsAt: day.AddHours(13), roomId: rooms["pool"]);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        var token = await FirstTokenAsync(client, day, day.AddDays(1), RoomQuery(rooms, firstRoom));

        using var response = await GetAsync(
            client,
            day.AddTicks(fromShift),
            day.AddDays(1).AddTicks(toShift),
            $"{RoomQuery(rooms, secondRoom)}&limit=1&continuationToken={token}");

        await QueryBindingTests.AssertSingleErrorAsync(response, "continuationToken", Mismatch);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Get_BadTokenAndToNotAfterFrom_ReturnsBothErrorsInOne400(bool parsable)
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room);
        await app.Data.ClassAsync(startsAt: day.AddHours(11), roomId: room);
        await app.Data.ClassAsync(startsAt: day.AddHours(12), roomId: room);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        var token = parsable ? await FirstTokenAsync(client, day, day.AddDays(1), "") : "abc%2B";

        using var response = await GetAsync(client, day.AddDays(2), day.AddDays(1), $"&continuationToken={token}");

        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, text);
        using var body = JsonDocument.Parse(text);
        var errors = body.RootElement.GetProperty("errors").EnumerateObject()
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => (x.Name, string.Join("|", x.Value.EnumerateArray().Select(m => m.GetString()))))
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                ("continuationToken", parsable ? Mismatch : "The value 'abc+' is not valid for continuationToken."),
                ("to", "The to field must be later than from.")
            },
            errors,
            text);
    }

    [TestMethod]
    public async Task Get_LastReturnedClassCancelledBetweenPages_NextPageStartsAfterIt()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var (room, ids) = await FourClassesAsync(app, day);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        var token = await FirstTokenAsync(client, day, day.AddDays(1), "");

        await CancelAsync(app, ids[1]);
        using var page2 = await GetAsync(client, day, day.AddDays(1), $"&limit=2&continuationToken={token}");

        await AssertPageAsync(
            page2,
            Item(ids[2], day.AddHours(12), room, "Hall"),
            Item(ids[3], day.AddHours(13), room, "Hall"));
    }

    [TestMethod]
    public async Task Get_ClassAheadOfCursorCancelled_IsNotReturned()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var (room, ids) = await FourClassesAsync(app, day);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        var token = await FirstTokenAsync(client, day, day.AddDays(1), "");

        await CancelAsync(app, ids[2]);
        using var page2 = await GetAsync(client, day, day.AddDays(1), $"&limit=2&continuationToken={token}");

        await AssertPageAsync(page2, Item(ids[3], day.AddHours(13), room, "Hall"));
    }

    [TestMethod]
    public async Task Get_ClassAddedBehindCursor_IsNotReturned()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var (room, ids) = await FourClassesAsync(app, day);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);
        var token = await FirstTokenAsync(client, day, day.AddDays(1), "");

        await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: room);
        await app.Data.ClassAsync(startsAt: day.AddHours(11).AddTicks(-10), roomId: room);
        using var page2 = await GetAsync(client, day, day.AddDays(1), $"&limit=2&continuationToken={token}");

        await AssertPageAsync(
            page2,
            Item(ids[2], day.AddHours(12), room, "Hall"),
            Item(ids[3], day.AddHours(13), room, "Hall"));
    }

    [TestMethod]
    public async Task Get_TokenOfAnotherUser_ContinuesWithOwnSeats()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var (room, ids) = await FourClassesAsync(app, day);
        var owner = await app.Data.UserAsync();
        var other = await app.Data.UserAsync();
        await app.Data.EnrollmentAsync(owner.Id, ids[2], 1);
        await app.Data.EnrollmentAsync(other.Id, ids[2], 3);
        using var ownerClient = await app.CreateClientAsync(owner.Email);
        using var otherClient = await app.CreateClientAsync(other.Email);
        var token = await FirstTokenAsync(ownerClient, day, day.AddDays(1), "");

        using var page2 = await GetAsync(otherClient, day, day.AddDays(1), $"&limit=2&continuationToken={token}");

        await AssertPageAsync(
            page2,
            Item(ids[2], day.AddHours(12), room, "Hall", freeSeats: 6, mySeats: 3),
            Item(ids[3], day.AddHours(13), room, "Hall"));
    }

    private static DateTime FarDay(TestApp app)
    {
        return app.SeededAt.UtcDateTime.Date.AddDays(400);
    }

    private static string Form(string format, DateTime day)
    {
        return Uri.EscapeDataString(string.Format(CultureInfo.InvariantCulture, format, day));
    }

    private static string RoomQuery(Dictionary<string, long> rooms, string? room)
    {
        return room is null ? "" : $"&roomId={rooms[room]}";
    }

    private static async Task<(long Room, long[] Ids)> FourClassesAsync(TestApp app, DateTime day)
    {
        var room = await app.Data.RoomAsync("Hall");
        var ids = new long[4];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = await app.Data.ClassAsync(startsAt: day.AddHours(10 + i), roomId: room);
        }

        return (room, ids);
    }

    private static async Task<string> FirstTokenAsync(HttpClient client, DateTime from, DateTime to, string extra)
    {
        using var response = await GetAsync(client, from, to, $"{extra}&limit={(extra.Length == 0 ? 2 : 1)}");
        var (_, token) = await PageAsync(response);
        Assert.IsNotNull(token, "the first page has no continuationToken.");

        return token;
    }

    private static async Task CancelAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();
        await context.Classes.Where(x => x.Id == classId).ExecuteUpdateAsync(x => x.SetProperty(c => c.IsCancelled, true));
    }

    private static async Task<long[]> ExpectedIdsAsync(TestApp app, DateTime from, DateTime to)
    {
        await using var context = app.CreateContext();

        return await context.Classes
            .Where(x => x.StartsAt >= from && x.StartsAt < to)
            .OrderBy(x => x.StartsAt)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .ToArrayAsync();
    }

    private static async Task<long[]> WalkAsync(HttpClient client, DateTime from, DateTime to, int limit)
    {
        var ids = new List<long>();
        string? token = null;

        for (var page = 0; page < 200; page++)
        {
            var extra = token is null ? $"&limit={limit}" : $"&limit={limit}&continuationToken={token}";
            using var response = await GetAsync(client, from, to, extra);
            var (pageIds, next) = await PageAsync(response);
            ids.AddRange(pageIds);

            if (next is null)
            {
                Assert.IsTrue(pageIds.Length <= limit);
                return ids.ToArray();
            }

            Assert.AreEqual(limit, pageIds.Length);
            token = next;
        }

        Assert.Fail("the walk did not end in 200 pages.");
        return [];
    }
}
