using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
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

    [TestMethod]
    public async Task Get_TwoRoomsThreeDays_ReturnsRowPerDayAndRoomOrderedByDateThenRoomId()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var first = await app.Data.RoomAsync("Zeta");
        var second = await app.Data.RoomAsync("Alpha");
        var u1 = await app.Data.UserAsync();
        var u2 = await app.Data.UserAsync();
        var lastSecond = await app.Data.ClassAsync(startsAt: day.AddDays(2).AddHours(7), roomId: second, capacity: 4);
        await app.Data.ClassAsync(startsAt: day.AddDays(2).AddHours(20), roomId: first, capacity: 6);
        await app.Data.ClassAsync(startsAt: day.AddDays(1).AddHours(12), roomId: second, capacity: 3);
        var lateFirst = await app.Data.ClassAsync(startsAt: day.AddHours(19), roomId: first, capacity: 5);
        await app.Data.ClassAsync(startsAt: day.AddHours(6), roomId: second, capacity: 8);
        var earlyFirst = await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: first, capacity: 10);
        await app.Data.EnrollmentAsync(u1.Id, earlyFirst, 3);
        await app.Data.EnrollmentAsync(u2.Id, earlyFirst, 2);
        await app.Data.EnrollmentAsync(u2.Id, lateFirst, 1);
        await app.Data.EnrollmentAsync(u1.Id, lastSecond, 4);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day.AddDays(2));

        await AssertItemsAsync(
            response,
            Item(day, first, "Zeta", 2, 15, 6),
            Item(day, second, "Alpha", 1, 8, 0),
            Item(day.AddDays(1), second, "Alpha", 1, 3, 0),
            Item(day.AddDays(2), first, "Zeta", 1, 6, 0),
            Item(day.AddDays(2), second, "Alpha", 1, 4, 4));
    }

    [TestMethod]
    public async Task Get_RoomFilter_ReturnsOnlyThatRoomRows()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        var pool = await app.Data.RoomAsync("Pool");
        await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: hall, capacity: 7);
        await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: pool, capacity: 9);
        await app.Data.ClassAsync(startsAt: day.AddDays(1).AddHours(10), roomId: pool, capacity: 2);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day.AddDays(1), $"&roomId={pool}");

        await AssertItemsAsync(response, Item(day, pool, "Pool", 1, 9, 0), Item(day.AddDays(1), pool, "Pool", 1, 2, 0));
    }

    [TestMethod]
    public async Task Get_SeededDatabase_MatchesAggregateOfActiveData()
    {
        await using var app = await TestApp.StartAsync();
        var from = app.SeededAt.UtcDateTime.Date.AddDays(-40);
        var to = app.SeededAt.UtcDateTime.Date.AddDays(40);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, from, to);

        await using var context = app.CreateContext();
        var classes = await context.Classes
            .Where(x => x.StartsAt >= from && x.StartsAt < to.AddDays(1))
            .Select(x => new { x.Id, x.StartsAt, x.RoomId, RoomName = x.Room.Name, x.Capacity })
            .ToListAsync();
        var enrollments = await context.Enrollments
            .Where(x => x.Status == EnrollmentStatuses.Active)
            .Select(x => new { x.ClassId, x.Seats })
            .ToListAsync();
        var expected = classes
            .GroupBy(x => (x.StartsAt.Date, x.RoomId, x.RoomName))
            .OrderBy(x => x.Key.Date)
            .ThenBy(x => x.Key.RoomId)
            .Select(x => Item(
                x.Key.Date,
                x.Key.RoomId,
                x.Key.RoomName,
                x.Count(),
                x.Sum(c => c.Capacity),
                x.Sum(c => enrollments.Where(e => e.ClassId == c.Id).Sum(e => e.Seats))))
            .ToArray();

        Assert.IsTrue(expected.Length > 20, $"only {expected.Length} rows in the seeded period");
        await AssertItemsAsync(response, expected);
    }

    [TestMethod]
    public async Task Get_CancelledClasses_CountNowhereAndGiveNoRow()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        var pool = await app.Data.RoomAsync("Pool");
        var user = await app.Data.UserAsync();
        await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: hall, capacity: 4);
        var cancelled = await app.Data.ClassAsync(startsAt: day.AddHours(11), roomId: hall, capacity: 50, cancelled: true);
        await app.Data.EnrollmentAsync(user.Id, cancelled, 3);
        await app.Data.ClassAsync(startsAt: day.AddHours(12), roomId: pool, capacity: 20, cancelled: true);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day);

        await AssertItemsAsync(response, Item(day, hall, "Hall", 1, 4, 0));
    }

    [TestMethod]
    public async Task Get_CancelledEnrollments_AreNotInBookedSeats()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        var u1 = await app.Data.UserAsync();
        var u2 = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: hall, capacity: 10);
        await app.Data.EnrollmentAsync(u1.Id, classId, 4, cancelled: true);
        await app.Data.EnrollmentAsync(u1.Id, classId, 1);
        await app.Data.EnrollmentAsync(u2.Id, classId, 2);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day);

        await AssertItemsAsync(response, Item(day, hall, "Hall", 1, 10, 3));
    }

    [TestMethod]
    [DataRow("9223372036854775807")]
    [DataRow("0")]
    [DataRow("-1")]
    public async Task Get_UnknownRoomWithDataInPeriod_ReturnsEmptyItems(string roomId)
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: hall);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day, $"&roomId={roomId}");

        await AssertItemsAsync(response);
    }

    [TestMethod]
    public async Task Get_ClassesOnPeriodBoundaries_FromAndToInclusiveByUtcDay()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        await app.Data.ClassAsync(startsAt: day.AddTicks(-10), roomId: hall, capacity: 100);
        await app.Data.ClassAsync(startsAt: day, roomId: hall, capacity: 1);
        await app.Data.ClassAsync(startsAt: day.AddDays(1).AddTicks(-10), roomId: hall, capacity: 2);
        await app.Data.ClassAsync(startsAt: day.AddDays(1), roomId: hall, capacity: 4);
        await app.Data.ClassAsync(startsAt: day.AddDays(2).AddTicks(-10), roomId: hall, capacity: 8);
        await app.Data.ClassAsync(startsAt: day.AddDays(2), roomId: hall, capacity: 200);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day.AddDays(1));

        await AssertItemsAsync(response, Item(day, hall, "Hall", 2, 3, 0), Item(day.AddDays(1), hall, "Hall", 2, 12, 0));
    }

    [TestMethod]
    public async Task Get_ClassOverMidnight_BelongsToStartDay()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        await app.Data.ClassAsync(startsAt: day.AddHours(23).AddMinutes(30), roomId: hall, durationMinutes: 120, capacity: 6);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day.AddDays(1));

        await AssertItemsAsync(response, Item(day, hall, "Hall", 1, 6, 0));
    }

    [TestMethod]
    public async Task Get_OverbookingInDatabase_BookedSeatsAboveCapacity()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: hall, capacity: 2);
        await app.Data.EnrollmentAsync(user.Id, classId, 5);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day);

        await AssertItemsAsync(response, Item(day, hall, "Hall", 1, 2, 5));
    }

    [TestMethod]
    public async Task Get_SumsOverIntMax_Saturate()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var hall = await app.Data.RoomAsync("Hall");
        var user = await app.Data.UserAsync();
        var a = await app.Data.ClassAsync(startsAt: day.AddHours(9), roomId: hall, capacity: int.MaxValue);
        var b = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: hall, capacity: int.MaxValue);
        await app.Data.EnrollmentAsync(user.Id, a, int.MaxValue);
        await app.Data.EnrollmentAsync(user.Id, b, int.MaxValue);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, day, day);

        await AssertItemsAsync(response, Item(day, hall, "Hall", 2, int.MaxValue, int.MaxValue));
    }

    [TestMethod]
    public async Task Get_LeapDayAndLastDate_RowsWithThoseDates()
    {
        await using var app = await TestApp.StartAsync();
        var hall = await app.Data.RoomAsync("Hall");
        var leap = new DateTime(2028, 2, 29, 0, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        await app.Data.ClassAsync(startsAt: leap.AddHours(10), roomId: hall, capacity: 3);
        await app.Data.ClassAsync(startsAt: last.AddHours(23), roomId: hall, capacity: 5);
        using var client = await app.CreateClientAsync(AuthClient.SeededAdmin);

        using var response = await GetAsync(client, $"from=0001-01-01&to=9999-12-31&roomId={hall}");

        await AssertItemsAsync(response, Item(leap, hall, "Hall", 1, 3, 0), Item(last, hall, "Hall", 1, 5, 0));
    }

    private static DateTime FarDay(TestApp app)
    {
        return app.SeededAt.UtcDateTime.Date.AddDays(400);
    }
}
