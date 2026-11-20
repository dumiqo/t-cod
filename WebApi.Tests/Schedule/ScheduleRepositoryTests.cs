using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Schedule;

[TestClass]
public class ScheduleRepositoryTests
{
    [TestMethod]
    public async Task GetSchedulePageAsync_Period_ReturnsRowsInHalfOpenRangeOrderedByStartsAtAndId()
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date.AddDays(400).AddTicks(50);
        var to = from.AddHours(1);
        await using var context = db.CreateContext();
        var room = await RoomAsync(context, "Hall");
        var late = await ClassAsync(context, room, to.AddTicks(-10));
        await ClassAsync(context, room, to);
        var sameFirst = await ClassAsync(context, room, from.AddMinutes(5));
        var sameSecond = await ClassAsync(context, room, from.AddMinutes(5));
        var atFrom = await ClassAsync(context, room, from);
        await ClassAsync(context, room, from.AddTicks(-10));

        var rows = await new UnitOfWork(context).Class.GetSchedulePageAsync(new ScheduleQuery(from, to, null, 10), 0);

        CollectionAssert.AreEqual(
            new[] { atFrom.Id, sameFirst.Id, sameSecond.Id, late.Id },
            rows.Select(x => x.Id).ToArray());
    }

    [TestMethod]
    public async Task GetSchedulePageAsync_Row_HasClassAndRoomFields()
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date.AddDays(400);
        await using var context = db.CreateContext();
        var room = await RoomAsync(context, "Pool");
        var item = await ClassAsync(context, room, from.AddTicks(1234560), title: "Swim", durationMinutes: 45, capacity: 7);

        var rows = await new UnitOfWork(context).Class.GetSchedulePageAsync(new ScheduleQuery(from, from.AddDays(1), null, 10), 0);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(new ScheduleRow(item.Id, "Swim", from.AddTicks(1234560), 45, 7, room.Id, "Pool", 0, 0), rows[0]);
    }

    [TestMethod]
    public async Task GetSchedulePageAsync_CancelledClass_IsNotReturned()
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date.AddDays(400);
        await using var context = db.CreateContext();
        var room = await RoomAsync(context, "Hall");
        await ClassAsync(context, room, from.AddHours(1), cancelled: true);
        var active = await ClassAsync(context, room, from.AddHours(2));

        var rows = await new UnitOfWork(context).Class.GetSchedulePageAsync(new ScheduleQuery(from, from.AddDays(1), null, 10), 0);

        CollectionAssert.AreEqual(new[] { active.Id }, rows.Select(x => x.Id).ToArray());
    }

    [TestMethod]
    public async Task GetSchedulePageAsync_RoomFilter_ReturnsOnlyThatRoom()
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date.AddDays(400);
        await using var context = db.CreateContext();
        var hall = await RoomAsync(context, "Hall");
        var pool = await RoomAsync(context, "Pool");
        var inHall = await ClassAsync(context, hall, from.AddHours(1));
        await ClassAsync(context, pool, from.AddHours(2));

        var rows = await new UnitOfWork(context).Class.GetSchedulePageAsync(new ScheduleQuery(from, from.AddDays(1), hall.Id, 10), 0);

        CollectionAssert.AreEqual(new[] { inHall.Id }, rows.Select(x => x.Id).ToArray());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task GetSchedulePageAsync_Take_ReturnsExactlyThatManyFirstRows(int take)
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date.AddDays(400);
        await using var context = db.CreateContext();
        var room = await RoomAsync(context, "Hall");
        var third = await ClassAsync(context, room, from.AddHours(3));
        var first = await ClassAsync(context, room, from.AddHours(1));
        var second = await ClassAsync(context, room, from.AddHours(2));

        var rows = await new UnitOfWork(context).Class.GetSchedulePageAsync(new ScheduleQuery(from, from.AddDays(1), null, take), 0);

        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id, third.Id }.Take(take).ToArray(),
            rows.Select(x => x.Id).ToArray());
    }

    [TestMethod]
    public async Task GetSchedulePageAsync_Sums_CountActiveSeatsOfClassAndOfUserInLong()
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date.AddDays(400);
        await using var context = db.CreateContext();
        var users = await context.Users.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToListAsync();
        var room = await RoomAsync(context, "Hall");
        var item = await ClassAsync(context, room, from.AddHours(1));
        var empty = await ClassAsync(context, room, from.AddHours(2));
        context.Enrollments.AddRange(
            Enrollment(users[0], item.Id, int.MaxValue),
            Enrollment(users[0], item.Id, int.MaxValue),
            Enrollment(users[1], item.Id, 5),
            Enrollment(users[0], item.Id, 7, cancelled: true),
            Enrollment(users[1], item.Id, 9, cancelled: true));
        await context.SaveChangesAsync();

        var rows = await new UnitOfWork(context).Class.GetSchedulePageAsync(new ScheduleQuery(from, from.AddDays(1), null, 10), users[0]);

        CollectionAssert.AreEqual(
            new[] { (item.Id, 2L * int.MaxValue + 5, 2L * int.MaxValue), (empty.Id, 0L, 0L) },
            rows.Select(x => (x.Id, x.TakenSeats, x.MySeats)).ToArray());
    }

    [TestMethod]
    public async Task GetSchedulePageAsync_CursorWithEqualStartsAt_ReturnsRowsStrictlyAfterCursorId()
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date.AddDays(400);
        var at = from.AddHours(10).AddTicks(10);
        await using var context = db.CreateContext();
        var room = await RoomAsync(context, "Hall");
        await ClassAsync(context, room, at.AddTicks(-10));
        var first = await ClassAsync(context, room, at);
        var second = await ClassAsync(context, room, at);
        var third = await ClassAsync(context, room, at);
        var later = await ClassAsync(context, room, at.AddTicks(10));

        var rows = await new UnitOfWork(context).Class.GetSchedulePageAsync(
            new ScheduleQuery(from, from.AddDays(1), null, 10, new ScheduleCursor(at, second.Id)), 0);

        Assert.IsTrue(first.Id < second.Id && second.Id < third.Id);
        CollectionAssert.AreEqual(new[] { third.Id, later.Id }, rows.Select(x => x.Id).ToArray());
    }

    [TestMethod]
    [DataRow(false, false, "IX_classes_starts_at_id")]
    [DataRow(false, true, "IX_classes_starts_at_id")]
    [DataRow(true, false, "IX_classes_room_id_starts_at_id")]
    [DataRow(true, true, "IX_classes_room_id_starts_at_id")]
    public async Task GetSchedulePageAsync_Plan_ReadsClassesAndSumsThroughScheduleIndexes(bool withRoom, bool withCursor, string classIndex)
    {
        await using var db = await TestDb.CloneAsync();
        var from = db.SeededAt.UtcDateTime.Date;
        await using (var setup = db.CreateContext())
        {
            // решение: на сиде (200 занятий, 10 залов) выбор индекса — дело случая; 18 тыс. занятий в 60 залах делают его устойчивым.
            await setup.Database.ExecuteSqlAsync($"INSERT INTO rooms (name) SELECT 'Room ' || g FROM generate_series(1, 50) g");
            await setup.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO classes (room_id, title, starts_at, duration_minutes, capacity, is_cancelled)
                SELECT r.id, 'Load', {from} + g * interval '1 hour' + r.id * interval '1 second', 60, 10, g % 10 = 0
                FROM rooms r CROSS JOIN generate_series(1, 300) g
                """);
            await setup.Database.ExecuteSqlRawAsync("VACUUM ANALYZE");
        }

        var recorder = new QueryPlanRecorder();
        await using var context = db.CreateContext(recorder);
        var query = new ScheduleQuery(from, from.AddDays(7), withRoom ? 1 : null, 21, withCursor ? new ScheduleCursor(from.AddDays(1), 50) : null);

        await new UnitOfWork(context).Class.GetSchedulePageAsync(query, 1);

        Assert.AreEqual(1, recorder.Plans.Count);
        Assert.AreEqual(
            string.Join(", ", new[] { classIndex, "IX_enrollments_class_id_user_id", "PK_rooms" }.Order()),
            string.Join(", ", QueryPlanRecorder.IndexNames(recorder.Plans[0]).Order()));
    }

    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

    private static async Task<Class> ClassAsync(
        ClubDbContext context,
        Room room,
        DateTime startsAt,
        bool cancelled = false,
        string title = "Test class",
        int durationMinutes = 60,
        int capacity = 10)
    {
        var item = new Class
        {
            RoomId = room.Id,
            Title = title,
            StartsAt = startsAt,
            DurationMinutes = durationMinutes,
            Capacity = capacity,
            IsCancelled = cancelled
        };
        context.Classes.Add(item);
        await context.SaveChangesAsync();

        return item;
    }
}
