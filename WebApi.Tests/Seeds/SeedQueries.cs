using System.Linq.Expressions;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Analytics;
using WebApi.Tests.Infrastructure;
using WebApi.Tests.Schedule;

namespace WebApi.Tests.Seeds;

public static class SeedQueries
{
    public static DateTime Now(TestApp app)
    {
        return app.Clock.GetUtcNow().UtcDateTime;
    }

    public static DateOnly Today(TestApp app)
    {
        return DateOnly.FromDateTime(Now(app));
    }

    public static async Task<Class> FreeSeatsClassAsync(TestApp app, int freeSeats, string scenario)
    {
        var now = Now(app);
        await using var context = app.CreateContext();
        var found = await context.Classes.IgnoreQueryFilters().AsNoTracking()
            .Where(x => !x.IsCancelled && x.StartsAt > now)
            .Where(x => x.Capacity - x.Enrollments.Where(e => e.Status == EnrollmentStatuses.Active).Sum(e => e.Seats) == freeSeats)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync();

        return found ?? Fail<Class>(scenario);
    }

    public static async Task<Class> ClassAtAsync(TestApp app, DateTime startsAt, string scenario)
    {
        await using var context = app.CreateContext();
        var found = await context.Classes.IgnoreQueryFilters().AsNoTracking()
            .Where(x => !x.IsCancelled && x.StartsAt == startsAt)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync();

        return found ?? Fail<Class>(scenario);
    }

    public static async Task<int> TakenSeatsAsync(TestApp app, long classId)
    {
        await using var context = app.CreateContext();

        return await context.Enrollments
            .Where(x => x.ClassId == classId && x.Status == EnrollmentStatuses.Active)
            .SumAsync(x => x.Seats);
    }

    public static async Task<TestUser> UserAsync(TestApp app, Expression<Func<Pass, bool>> pass, long? notOnClassId, string scenario)
    {
        await using var context = app.CreateContext();
        var users = context.Users.AsNoTracking()
            .Where(x => x.Role == "user")
            .Where(x => context.Passes.Where(pass).Any(p => p.UserId == x.Id));

        if (notOnClassId is { } classId)
        {
            users = users
                .Where(x => !context.Enrollments.Any(e => e.UserId == x.Id && e.ClassId == classId))
                .Where(x => !context.WaitlistEntries.Any(e => e.UserId == x.Id && e.ClassId == classId));
        }

        var found = await users.OrderBy(x => x.Id).Select(x => new TestUser(x.Id, x.Email)).FirstOrDefaultAsync();

        return found ?? Fail<TestUser>(scenario);
    }

    public static Task<TestUser> ValidUserAsync(TestApp app, int visits, long classId)
    {
        var today = Today(app);

        return UserAsync(
            app,
            x => x.RemainingVisits >= visits && x.ValidUntil >= today,
            classId,
            $"в сиде нет пользователя с действующим абонементом на {visits} посещений");
    }

    public static async Task<Enrollment> ActiveEnrollmentAsync(TestApp app, Expression<Func<Enrollment, bool>> filter, string scenario)
    {
        await using var context = app.CreateContext();
        var found = await context.Enrollments.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Class)
            .Where(x => x.Status == EnrollmentStatuses.Active)
            .Where(filter)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync();

        return found ?? Fail<Enrollment>(scenario);
    }

    public static async Task<TestUser> OwnerAsync(TestApp app, long enrollmentId)
    {
        await using var context = app.CreateContext();
        var userId = await context.Enrollments.Where(x => x.Id == enrollmentId).Select(x => x.UserId).SingleAsync();

        return await context.Users.Where(x => x.Id == userId).Select(x => new TestUser(x.Id, x.Email)).SingleAsync();
    }

    public static async Task<Class[]> SameStartPairAsync(TestApp app, bool withCancelled, string scenario)
    {
        var now = Now(app);
        await using var context = app.CreateContext();
        var classes = await context.Classes.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var pair = classes
            .GroupBy(x => x.StartsAt)
            .Where(x => x.Count() == 2)
            .Where(x => withCancelled ? x.Count(c => c.IsCancelled) == 1 : x.All(c => !c.IsCancelled) && x.Key > now)
            .Select(x => x.ToArray())
            .FirstOrDefault();

        return pair ?? Fail<Class[]>(scenario);
    }

    public static async Task<object> ScheduleItemAsync(TestApp app, long classId, long userId)
    {
        await using var context = app.CreateContext();
        var item = await context.Classes.IgnoreQueryFilters().AsNoTracking().Include(x => x.Room).SingleAsync(x => x.Id == classId);
        var active = context.Enrollments.Where(x => x.ClassId == classId && x.Status == EnrollmentStatuses.Active);
        var taken = await active.SumAsync(x => x.Seats);
        var mine = await active.Where(x => x.UserId == userId).SumAsync(x => x.Seats);

        return ScheduleApi.Item(
            item.Id,
            item.StartsAt,
            item.RoomId,
            item.Room.Name,
            item.Title,
            item.DurationMinutes,
            item.Capacity,
            item.Capacity - taken,
            mine);
    }

    public static async Task<object[]> RoomLoadItemsAsync(TestApp app, DateTime day)
    {
        var from = day.Date;
        var to = from.AddDays(1);
        await using var context = app.CreateContext();
        var classes = await context.Classes.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Room)
            .Where(x => !x.IsCancelled && x.StartsAt >= from && x.StartsAt < to)
            .Select(x => new
            {
                x.RoomId,
                x.Room.Name,
                x.Capacity,
                Booked = x.Enrollments.Where(e => e.Status == EnrollmentStatuses.Active).Sum(e => e.Seats)
            })
            .ToListAsync();

        return classes
            .GroupBy(x => (x.RoomId, x.Name))
            .OrderBy(x => x.Key.RoomId)
            .Select(x => RoomLoadApi.Item(from, x.Key.RoomId, x.Key.Name, x.Count(), x.Sum(c => c.Capacity), x.Sum(c => c.Booked)))
            .ToArray();
    }

    private static T Fail<T>(string scenario)
    {
        Assert.Fail(scenario);

        throw new InvalidOperationException(scenario);
    }
}
