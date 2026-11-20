using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Analytics;

[TestClass]
public class RoomLoadRepositoryTests
{
    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task GetRoomLoadAsync_Period_GroupsByUtcDayAndRoomInHalfOpenRange()
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date.AddDays(400).AddTicks(10);
        var toExclusive = from.AddDays(1);
        var day = DateOnly.FromDateTime(from);
        await using var context = db.CreateContext();
        var pool = await RoomAsync(context, "Pool");
        var hall = await RoomAsync(context, "Hall");
        await ClassAsync(context, hall, from.AddTicks(-10), capacity: 100);
        await ClassAsync(context, hall, from, capacity: 1);
        await ClassAsync(context, pool, from.AddHours(3), capacity: 2);
        await ClassAsync(context, hall, from.AddHours(5), capacity: 4);
        await ClassAsync(context, hall, toExclusive.AddTicks(-10), capacity: 8);
        await ClassAsync(context, hall, toExclusive, capacity: 200);

        var rows = await new UnitOfWork(context).Class.GetRoomLoadAsync(from, toExclusive, null);

        CollectionAssert.AreEqual(
            new[]
            {
                new RoomLoadRow(day, pool.Id, "Pool", 1, 2, 0),
                new RoomLoadRow(day, hall.Id, "Hall", 2, 5, 0),
                new RoomLoadRow(day.AddDays(1), hall.Id, "Hall", 1, 8, 0)
            },
            rows);
    }

    [TestMethod]
    public async Task GetRoomLoadAsync_RoomFilter_ReturnsOnlyThatRoom()
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date.AddDays(400);
        await using var context = db.CreateContext();
        var hall = await RoomAsync(context, "Hall");
        var pool = await RoomAsync(context, "Pool");
        await ClassAsync(context, hall, from.AddHours(1), capacity: 3);
        await ClassAsync(context, pool, from.AddHours(2), capacity: 5);

        var rows = await new UnitOfWork(context).Class.GetRoomLoadAsync(from, from.AddDays(1), pool.Id);

        CollectionAssert.AreEqual(new[] { new RoomLoadRow(DateOnly.FromDateTime(from), pool.Id, "Pool", 1, 5, 0) }, rows);
    }

    [TestMethod]
    public async Task GetRoomLoadAsync_CancelledClassesAndEnrollments_AreExcluded()
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date.AddDays(400);
        await using var context = db.CreateContext();
        var hall = await RoomAsync(context, "Hall");
        var pool = await RoomAsync(context, "Pool");
        var active = await ClassAsync(context, hall, from.AddHours(1), capacity: 10);
        var cancelled = await ClassAsync(context, hall, from.AddHours(2), cancelled: true, capacity: 50);
        await ClassAsync(context, pool, from.AddHours(3), cancelled: true);
        context.Enrollments.AddRange(
            Enrollment(1, active.Id, 2),
            Enrollment(2, active.Id, 4, cancelled: true),
            Enrollment(3, cancelled.Id, 6));
        await context.SaveChangesAsync();

        var rows = await new UnitOfWork(context).Class.GetRoomLoadAsync(from, from.AddDays(1), null);

        CollectionAssert.AreEqual(new[] { new RoomLoadRow(DateOnly.FromDateTime(from), hall.Id, "Hall", 1, 10, 2) }, rows);
    }

    [TestMethod]
    public async Task GetRoomLoadAsync_SumsOverIntMax_ReturnedAsLongWithoutOverflow()
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date.AddDays(400);
        await using var context = db.CreateContext();
        var hall = await RoomAsync(context, "Hall");
        var a = await ClassAsync(context, hall, from.AddHours(1), capacity: int.MaxValue);
        var b = await ClassAsync(context, hall, from.AddHours(2), capacity: int.MaxValue);
        context.Enrollments.AddRange(
            Enrollment(1, a.Id, int.MaxValue),
            Enrollment(2, a.Id, int.MaxValue),
            Enrollment(1, b.Id, int.MaxValue));
        await context.SaveChangesAsync();

        var rows = await new UnitOfWork(context).Class.GetRoomLoadAsync(from, from.AddDays(1), null);

        CollectionAssert.AreEqual(
            new[] { new RoomLoadRow(DateOnly.FromDateTime(from), hall.Id, "Hall", 2, 2L * int.MaxValue, 3L * int.MaxValue) },
            rows);
    }

    [TestMethod]
    [DataRow("Pacific/Kiritimati")]
    [DataRow("America/Los_Angeles")]
    public async Task GetRoomLoadAsync_SessionTimeZone_DayIsUtcDate(string timeZone)
    {
        await using var db = await TestDb.CloneAsync();
        var day = db.SeededAt.UtcDateTime.Date.AddDays(400);
        var connectionString = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Timezone = timeZone }.ConnectionString;
        await using var context = new ClubDbContext(new DbContextOptionsBuilder<ClubDbContext>().UseNpgsql(connectionString).Options);
        var hall = await RoomAsync(context, "Hall");
        await ClassAsync(context, hall, day.AddHours(23).AddMinutes(30), capacity: 3);
        await ClassAsync(context, hall, day.AddDays(1).AddMinutes(30), capacity: 5);

        var rows = await new UnitOfWork(context).Class.GetRoomLoadAsync(day, day.AddDays(2), null);

        CollectionAssert.AreEqual(
            new[]
            {
                new RoomLoadRow(DateOnly.FromDateTime(day), hall.Id, "Hall", 1, 3, 0),
                new RoomLoadRow(DateOnly.FromDateTime(day).AddDays(1), hall.Id, "Hall", 1, 5, 0)
            },
            rows);
    }

    private static Enrollment Enrollment(long userId, long classId, int seats, bool cancelled = false)
    {
        return new Enrollment
        {
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            Status = cancelled ? "cancelled" : EnrollmentStatuses.Active,
            CreatedAt = CreatedAt,
            CancelledAt = cancelled ? CreatedAt : null,
            RefundedVisits = cancelled ? seats : null
        };
    }

    private static async Task<Room> RoomAsync(ClubDbContext context, string name)
    {
        var room = new Room { Name = name };
        context.Rooms.Add(room);
        await context.SaveChangesAsync();

        return room;
    }

    private static async Task<Class> ClassAsync(ClubDbContext context, Room room, DateTime startsAt, bool cancelled = false, int capacity = 10)
    {
        var item = new Class
        {
            RoomId = room.Id,
            Title = "Test class",
            StartsAt = startsAt,
            DurationMinutes = 60,
            Capacity = capacity,
            IsCancelled = cancelled
        };
        context.Classes.Add(item);
        await context.SaveChangesAsync();

        return item;
    }
}
