using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class EnrollmentRepositoryTests
{
    [TestMethod]
    public async Task SumActiveSeatsAsync_ActiveAndCancelled_SumsOnlyActive()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await AddClassAsync(context);
        await AddEnrollmentAsync(context, classId, 2, "active");
        await AddEnrollmentAsync(context, classId, 3, "active");
        await AddEnrollmentAsync(context, classId, 4, "cancelled");

        var sum = await new UnitOfWork(context).Enrollment.SumActiveSeatsAsync(classId);

        Assert.AreEqual(5L, sum);
    }

    [TestMethod]
    public async Task SumActiveSeatsAsync_OtherClassEnrollments_NotCounted()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await AddClassAsync(context);
        var otherId = await AddClassAsync(context);
        await AddEnrollmentAsync(context, classId, 2, "active");
        await AddEnrollmentAsync(context, otherId, 7, "active");

        var sum = await new UnitOfWork(context).Enrollment.SumActiveSeatsAsync(classId);

        Assert.AreEqual(2L, sum);
    }

    [TestMethod]
    public async Task SumActiveSeatsAsync_NoEnrollments_ReturnsZero()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await AddClassAsync(context);
        await AddEnrollmentAsync(context, classId, 4, "cancelled");

        var sum = await new UnitOfWork(context).Enrollment.SumActiveSeatsAsync(classId);

        Assert.AreEqual(0L, sum);
    }

    [TestMethod]
    public async Task SumActiveSeatsAsync_TwoMaxSeatEnrollments_DoesNotOverflow()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await AddClassAsync(context, capacity: int.MaxValue);
        await AddEnrollmentAsync(context, classId, int.MaxValue, "active");
        await AddEnrollmentAsync(context, classId, int.MaxValue, "active");

        var sum = await new UnitOfWork(context).Enrollment.SumActiveSeatsAsync(classId);

        Assert.AreEqual(4294967294L, sum);
    }

    [TestMethod]
    public async Task SumActiveSeatsAsync_CancelledClass_CountsItsActiveEnrollments()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await AddClassAsync(context, cancelled: true);
        await AddEnrollmentAsync(context, classId, 3, "active");

        var sum = await new UnitOfWork(context).Enrollment.SumActiveSeatsAsync(classId);

        Assert.AreEqual(3L, sum);
    }

    [TestMethod]
    [DataRow("active", false)]
    [DataRow("cancelled", false)]
    [DataRow("active", true)]
    [DataRow("cancelled", true)]
    public async Task GetCardAsync_OwnEnrollment_ReturnsEnrollmentClassRoomAndPass(string status, bool classCancelled)
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await AddClassAsync(context, cancelled: classCancelled);
        var user = await context.Users.OrderBy(x => x.Id).FirstAsync();
        var id = await AddOwnEnrollmentAsync(context, user.Id, classId, 3, status);
        var pass = await context.Passes.AsNoTracking().SingleAsync(x => x.UserId == user.Id);
        var room = await context.Rooms.AsNoTracking().OrderBy(x => x.Id).FirstAsync();

        var card = await new UnitOfWork(context).Enrollment.GetCardAsync(id, user.Id);

        Assert.AreEqual(
            new EnrollmentCard(
                id,
                status,
                3,
                new DateTime(2029, 12, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560),
                new EnrollmentCardClass(
                    classId,
                    "Test class",
                    new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc),
                    60,
                    classCancelled,
                    new EnrollmentCardRoom(room.Id, room.Name)),
                new EnrollmentCardPass(pass.RemainingVisits, pass.ValidUntil)),
            card);
    }

    [TestMethod]
    public async Task GetCardAsync_OtherUsersEnrollment_ReturnsNull()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await AddClassAsync(context);
        var users = await context.Users.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToListAsync();
        var id = await AddOwnEnrollmentAsync(context, users[0], classId, 1, "active");

        var card = await new UnitOfWork(context).Enrollment.GetCardAsync(id, users[1]);

        Assert.IsNull(card);
    }

    [TestMethod]
    [DataRow(999999999L)]
    [DataRow(0L)]
    [DataRow(-1L)]
    public async Task GetCardAsync_UnknownId_ReturnsNull(long id)
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var userId = await context.Users.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();

        var card = await new UnitOfWork(context).Enrollment.GetCardAsync(id, userId);

        Assert.IsNull(card);
    }

    [TestMethod]
    public async Task GetCardAsync_Found_ExecutesExactlyOneCommand()
    {
        await using var db = await TestDb.CloneAsync();
        long id;
        long userId;
        await using (var setup = db.CreateContext())
        {
            var classId = await AddClassAsync(setup, cancelled: true);
            userId = await setup.Users.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
            id = await AddOwnEnrollmentAsync(setup, userId, classId, 2, "active");
        }

        var counter = new CommandCounter();
        await using var context = db.CreateContext(counter);

        var card = await new UnitOfWork(context).Enrollment.GetCardAsync(id, userId);

        Assert.AreEqual(id, card?.Id);
        Assert.AreEqual(1, counter.Count);
    }

    [TestMethod]
    [DataRow("active", false)]
    [DataRow("cancelled", false)]
    [DataRow("active", true)]
    [DataRow("cancelled", true)]
    public async Task FindOwnedAsync_OwnEnrollment_ReturnsUntrackedEnrollment(string status, bool classCancelled)
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await AddClassAsync(context, cancelled: classCancelled);
        var userId = await context.Users.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        var id = await AddOwnEnrollmentAsync(context, userId, classId, 3, status);

        var enrollment = await new UnitOfWork(context).Enrollment.FindOwnedAsync(id, userId);

        Assert.IsNotNull(enrollment);
        var createdAt = new DateTime(2029, 12, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560);
        Assert.AreEqual(id, enrollment.Id);
        Assert.AreEqual(userId, enrollment.UserId);
        Assert.AreEqual(classId, enrollment.ClassId);
        Assert.AreEqual(3, enrollment.Seats);
        Assert.AreEqual(status, enrollment.Status);
        Assert.AreEqual(createdAt, enrollment.CreatedAt);
        Assert.AreEqual(status == "cancelled" ? createdAt : null, enrollment.CancelledAt);
        Assert.AreEqual(status == "cancelled" ? 3 : null, enrollment.RefundedVisits);
        Assert.IsNull(enrollment.Class);
        Assert.AreEqual(0, context.ChangeTracker.Entries().Count());
    }

    [TestMethod]
    public async Task FindOwnedAsync_OtherUsersEnrollment_ReturnsNull()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await AddClassAsync(context);
        var users = await context.Users.OrderBy(x => x.Id).Select(x => x.Id).Take(2).ToListAsync();
        var id = await AddOwnEnrollmentAsync(context, users[0], classId, 1, "active");

        var enrollment = await new UnitOfWork(context).Enrollment.FindOwnedAsync(id, users[1]);

        Assert.IsNull(enrollment);
    }

    [TestMethod]
    [DataRow(999999999L)]
    [DataRow(0L)]
    [DataRow(-1L)]
    public async Task FindOwnedAsync_UnknownId_ReturnsNull(long id)
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var userId = await context.Users.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();

        var enrollment = await new UnitOfWork(context).Enrollment.FindOwnedAsync(id, userId);

        Assert.IsNull(enrollment);
    }

    private static async Task<long> AddOwnEnrollmentAsync(ClubDbContext context, long userId, long classId, int seats, string status)
    {
        var createdAt = new DateTime(2029, 12, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560);
        var enrollment = new Enrollment
        {
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            Status = status,
            CreatedAt = createdAt,
            CancelledAt = status == "cancelled" ? createdAt : null,
            RefundedVisits = status == "cancelled" ? seats : null
        };
        context.Enrollments.Add(enrollment);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return enrollment.Id;
    }

    private static async Task<long> AddClassAsync(ClubDbContext context, int capacity = 10, bool cancelled = false)
    {
        var roomId = await context.Rooms.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        var item = new Class
        {
            RoomId = roomId,
            Title = "Test class",
            StartsAt = new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            DurationMinutes = 60,
            Capacity = capacity,
            IsCancelled = cancelled
        };
        context.Classes.Add(item);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return item.Id;
    }

    private static async Task AddEnrollmentAsync(ClubDbContext context, long classId, int seats, string status)
    {
        var userId = await context.Users.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        var createdAt = new DateTime(2029, 12, 1, 10, 0, 0, DateTimeKind.Utc);
        context.Enrollments.Add(new Enrollment
        {
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            Status = status,
            CreatedAt = createdAt,
            CancelledAt = status == "cancelled" ? createdAt : null,
            RefundedVisits = status == "cancelled" ? seats : null
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }
}
