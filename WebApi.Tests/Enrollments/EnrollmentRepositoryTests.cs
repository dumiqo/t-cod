using DataAccessLayer;
using DataAccessLayer.Entities;
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
            CancelledAt = status == "cancelled" ? createdAt : null
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }
}
