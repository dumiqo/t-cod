using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Schedule.ScheduleApi;

namespace WebApi.Tests.Schedule;

[TestClass]
public class ScheduleSeatsTests
{
    [TestMethod]
    public async Task Get_SeededDatabase_SeatsMatchActiveEnrollments()
    {
        await using var app = await TestApp.StartAsync();
        var from = app.SeededAt.UtcDateTime.AddDays(-5);
        var to = from.AddDays(60);
        using var client = await app.CreateClientAsync(AuthClient.SeededUser);

        using var response = await GetAsync(client, from, to, "&limit=100");

        await using var context = app.CreateContext();
        var userId = await context.Users.Where(x => x.Email == AuthClient.SeededUser).Select(x => x.Id).SingleAsync();
        var classes = await context.Classes
            .Where(x => x.StartsAt >= from && x.StartsAt < to)
            .OrderBy(x => x.StartsAt)
            .ThenBy(x => x.Id)
            .Take(100)
            .Select(x => new { x.Id, x.Capacity })
            .ToListAsync();
        var classIds = classes.Select(x => x.Id).ToList();
        var enrollments = await context.Enrollments
            .Where(x => classIds.Contains(x.ClassId) && x.Status == EnrollmentStatuses.Active)
            .Select(x => new { x.ClassId, x.UserId, x.Seats })
            .ToListAsync();
        var expected = classes
            .Select(x => (
                x.Id,
                FreeSeats: Math.Max(0, x.Capacity - enrollments.Where(e => e.ClassId == x.Id).Sum(e => e.Seats)),
                MySeats: enrollments.Where(e => e.ClassId == x.Id && e.UserId == userId).Sum(e => e.Seats)))
            .ToArray();

        Assert.AreEqual(100, expected.Length);
        CollectionAssert.IsSubsetOf(new[] { 0, 1, 2 }, expected.Select(x => x.FreeSeats).ToArray());
        Assert.IsTrue(expected.Any(x => x.MySeats > 0), "user001 has no active seats on the page.");
        CollectionAssert.AreEqual(expected, await SeatsAsync(response));
    }

    [TestMethod]
    public async Task Get_TwoOwnEnrollmentsOnClass_MySeatsIsTheirSum()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var classId = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room, capacity: 10);
        var user = await app.Data.UserAsync();
        await app.Data.EnrollmentAsync(user.Id, classId, 2);
        await app.Data.EnrollmentAsync(user.Id, classId, 3);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await GetAsync(client, day, day.AddDays(1));

        await AssertPageAsync(response, Item(classId, day.AddHours(10), room, "Hall", freeSeats: 5, mySeats: 5));
    }

    [TestMethod]
    public async Task Get_OtherUsersEnrollment_CountsInFreeSeatsButNotInMySeats()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var classId = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room, capacity: 10);
        var user = await app.Data.UserAsync();
        var other = await app.Data.UserAsync();
        await app.Data.EnrollmentAsync(user.Id, classId, 1);
        await app.Data.EnrollmentAsync(other.Id, classId, 4);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await GetAsync(client, day, day.AddDays(1));

        await AssertPageAsync(response, Item(classId, day.AddHours(10), room, "Hall", freeSeats: 5, mySeats: 1));
    }

    [TestMethod]
    public async Task Get_CancelledEnrollments_CountNeitherInFreeSeatsNorInMySeats()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var classId = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room, capacity: 10);
        var user = await app.Data.UserAsync();
        var other = await app.Data.UserAsync();
        await app.Data.EnrollmentAsync(user.Id, classId, 1);
        await app.Data.EnrollmentAsync(user.Id, classId, 3, cancelled: true);
        await app.Data.EnrollmentAsync(other.Id, classId, 4, cancelled: true);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await GetAsync(client, day, day.AddDays(1));

        await AssertPageAsync(response, Item(classId, day.AddHours(10), room, "Hall", freeSeats: 9, mySeats: 1));
    }

    [TestMethod]
    public async Task Get_ClassWithoutEnrollmentsNextToBookedClass_HasFullCapacityAndNoOwnSeats()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var booked = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room, capacity: 10);
        var empty = await app.Data.ClassAsync(startsAt: day.AddHours(11), roomId: room, capacity: 7);
        var user = await app.Data.UserAsync();
        await app.Data.EnrollmentAsync(user.Id, booked, 2);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await GetAsync(client, day, day.AddDays(1));

        await AssertPageAsync(
            response,
            Item(booked, day.AddHours(10), room, "Hall", freeSeats: 8, mySeats: 2),
            Item(empty, day.AddHours(11), room, "Hall", capacity: 7, freeSeats: 7, mySeats: 0));
    }

    [TestMethod]
    [DataRow(4, 1)]
    [DataRow(5, 0)]
    [DataRow(6, 0)]
    public async Task Get_TakenSeatsAroundCapacity_FreeSeatsIsNotBelowZero(int taken, int freeSeats)
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var classId = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room, capacity: 5);
        var other = await app.Data.UserAsync();
        await app.Data.EnrollmentAsync(other.Id, classId, taken);
        var user = await app.Data.UserAsync();
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await GetAsync(client, day, day.AddDays(1));

        await AssertPageAsync(response, Item(classId, day.AddHours(10), room, "Hall", capacity: 5, freeSeats: freeSeats));
    }

    [TestMethod]
    public async Task Get_OwnSeatsOverIntMax_MySeatsSaturatesAndFreeSeatsIsZero()
    {
        await using var app = await TestApp.StartAsync();
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var classId = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room, capacity: 10);
        var user = await app.Data.UserAsync();
        await app.Data.EnrollmentAsync(user.Id, classId, int.MaxValue);
        await app.Data.EnrollmentAsync(user.Id, classId, int.MaxValue);
        using var client = await app.CreateClientAsync(user.Email);

        using var response = await GetAsync(client, day, day.AddDays(1));

        await AssertPageAsync(response, Item(classId, day.AddHours(10), room, "Hall", freeSeats: 0, mySeats: int.MaxValue));
    }

    [TestMethod]
    public async Task Get_UserWithManyEnrollments_ExecutesOneCommand()
    {
        await using var app = await TestApp.StartAsync(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Information"
                })));
        var day = FarDay(app);
        var room = await app.Data.RoomAsync("Hall");
        var user = await app.Data.UserAsync();
        await using (var context = app.CreateContext())
        {
            var others = Enumerable.Range(0, 100)
                .Select(i => new Class
                {
                    RoomId = room,
                    Title = "Other",
                    StartsAt = day.AddDays(2).AddMinutes(i),
                    DurationMinutes = 60,
                    Capacity = 10
                })
                .ToList();
            context.Classes.AddRange(others);
            await context.SaveChangesAsync();
            context.Enrollments.AddRange(others.Select(x => new Enrollment
            {
                UserId = user.Id,
                ClassId = x.Id,
                Seats = 1,
                Status = EnrollmentStatuses.Active,
                CreatedAt = app.SeededAt.UtcDateTime
            }));
            await context.SaveChangesAsync();
        }

        var classId = await app.Data.ClassAsync(startsAt: day.AddHours(10), roomId: room, capacity: 10);
        await app.Data.EnrollmentAsync(user.Id, classId, 2);
        using var client = await app.CreateClientAsync(user.Email);
        app.Logs.Clear();

        using var response = await GetAsync(client, day, day.AddDays(1));

        await AssertPageAsync(response, Item(classId, day.AddHours(10), room, "Hall", freeSeats: 8, mySeats: 2));
        Assert.AreEqual(1, app.Logs.GetSnapshot().Count(x => x.Id.Id == RelationalEventId.CommandExecuted.Id));
    }

    private static DateTime FarDay(TestApp app)
    {
        return app.SeededAt.UtcDateTime.Date.AddDays(400);
    }

    private static async Task<(long Id, int FreeSeats, int MySeats)[]> SeatsAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        using var body = JsonDocument.Parse(text);

        return body.RootElement.GetProperty("items").EnumerateArray()
            .Select(x => (
                x.GetProperty("classId").GetInt64(),
                x.GetProperty("freeSeats").GetInt32(),
                x.GetProperty("mySeats").GetInt32()))
            .ToArray();
    }
}
