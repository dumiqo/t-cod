using DataAccessLayer.Entities;
using DataAccessLayer.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests.Infrastructure;

public sealed class TestData
{
    private static readonly string PasswordHash =
        new PasswordHasher<User>().HashPassword(new User(), DatabaseSeeder.DefaultPassword);

    private readonly TestApp _app;

    public TestData(TestApp app)
    {
        _app = app;
    }

    public async Task<TestUser> UserAsync(int remainingVisits = 10, DateOnly? validUntil = null)
    {
        var now = _app.Clock.GetUtcNow().UtcDateTime;
        var user = new User
        {
            Email = $"test-{Guid.NewGuid():N}@club.test",
            PasswordHash = PasswordHash,
            Role = "user",
            CreatedAt = now
        };

        await using var context = _app.CreateContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        context.Passes.Add(new Pass
        {
            UserId = user.Id,
            RemainingVisits = remainingVisits,
            ValidUntil = validUntil ?? DateOnly.FromDateTime(now).AddDays(30)
        });
        await context.SaveChangesAsync();

        return new TestUser(user.Id, user.Email);
    }

    public async Task<long> RoomAsync(string name)
    {
        var room = new Room { Name = name };

        await using var context = _app.CreateContext();
        context.Rooms.Add(room);
        await context.SaveChangesAsync();

        return room.Id;
    }

    public async Task<long> ClassAsync(
        int capacity = 10,
        DateTime? startsAt = null,
        bool cancelled = false,
        long? roomId = null,
        string title = "Test class",
        int durationMinutes = 60)
    {
        await using var context = _app.CreateContext();
        var item = new Class
        {
            RoomId = roomId ?? await context.Rooms.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync(),
            Title = title,
            StartsAt = startsAt ?? _app.Clock.GetUtcNow().UtcDateTime.AddDays(1),
            DurationMinutes = durationMinutes,
            Capacity = capacity,
            IsCancelled = cancelled
        };

        context.Classes.Add(item);
        await context.SaveChangesAsync();

        return item.Id;
    }

    public async Task<long> EnrollmentAsync(long userId, long classId, int seats, bool cancelled = false, int? refundedVisits = null)
    {
        var now = _app.Clock.GetUtcNow().UtcDateTime;
        var enrollment = new Enrollment
        {
            UserId = userId,
            ClassId = classId,
            Seats = seats,
            Status = cancelled ? "cancelled" : EnrollmentStatuses.Active,
            CreatedAt = now,
            CancelledAt = cancelled ? now : null,
            RefundedVisits = cancelled ? refundedVisits ?? seats : null
        };

        await using var context = _app.CreateContext();
        context.Enrollments.Add(enrollment);
        await context.SaveChangesAsync();

        return enrollment.Id;
    }
}

public sealed record TestUser(long Id, string Email);
