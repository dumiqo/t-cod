using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Existing;

[TestClass]
public class RepositoryTests
{
    [TestMethod]
    public async Task ClassGetById_CancelledClass_IsHiddenByQueryFilter()
    {
        using var scope = ApiHost.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var cancelledId = await dbContext.Classes
            .IgnoreQueryFilters()
            .Where(x => x.IsCancelled)
            .Select(x => x.Id)
            .FirstAsync();

        Assert.IsNull(await unitOfWork.Class.GetByIdAsync(cancelledId));
        Assert.IsNull(await unitOfWork.Class.GetWithEnrollmentsAsync(cancelledId));
    }

    [TestMethod]
    public async Task EnrollmentWithClassInclude_CancelledClass_IsHiddenByQueryFilter()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await using var dbContext = database.CreateContext();
        await dbContext.Database.MigrateAsync();

        var user = new User
        {
            Email = "scratch@club.test",
            PasswordHash = "-",
            Role = "user",
            CreatedAt = DateTime.UtcNow
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var enrollment = new Enrollment
        {
            UserId = user.Id,
            Seats = 1,
            Status = "active",
            CreatedAt = DateTime.UtcNow,
            Class = new Class
            {
                Room = new Room { Name = "Scratch room" },
                Title = "Cancelled by club",
                StartsAt = DateTime.UtcNow.AddDays(1),
                DurationMinutes = 60,
                Capacity = 10,
                IsCancelled = true
            }
        };

        dbContext.Enrollments.Add(enrollment);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var loaded = await dbContext.Enrollments
            .Include(x => x.Class)
            .FirstOrDefaultAsync(x => x.Id == enrollment.Id);

        Assert.IsNull(loaded);
        Assert.IsNotNull(await dbContext.Enrollments.FirstOrDefaultAsync(x => x.Id == enrollment.Id));
    }

    [TestMethod]
    public async Task ClassGetWithEnrollments_ActiveClass_LoadsAllEnrollments()
    {
        using var scope = ApiHost.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var classId = await dbContext.Enrollments
            .Where(x => !x.Class.IsCancelled)
            .Select(x => x.ClassId)
            .FirstAsync();

        var expected = await dbContext.Enrollments.CountAsync(x => x.ClassId == classId);
        var loaded = await unitOfWork.Class.GetWithEnrollmentsAsync(classId);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(expected, loaded.Enrollments.Count);
    }

    [TestMethod]
    public async Task PassGetByUser_ReturnsUsersPass()
    {
        using var scope = ApiHost.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var userId = await dbContext.Users.Select(x => x.Id).FirstAsync();

        var pass = await unitOfWork.Pass.GetByUserAsync(userId);

        Assert.IsNotNull(pass);
        Assert.AreEqual(userId, pass.UserId);
    }

    [TestMethod]
    public async Task EnrollmentGetByUser_ReturnsOnlyUsersEnrollments()
    {
        using var scope = ApiHost.Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var userId = await dbContext.Enrollments.Select(x => x.UserId).FirstAsync();
        var expected = await dbContext.Enrollments.CountAsync(x => x.UserId == userId);

        var enrollments = await unitOfWork.Enrollment.GetByUserAsync(userId);

        Assert.AreEqual(expected, enrollments.Count);
        Assert.IsTrue(enrollments.All(x => x.UserId == userId));
    }
}
