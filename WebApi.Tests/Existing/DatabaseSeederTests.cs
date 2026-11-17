using DataAccessLayer;
using DataAccessLayer.Seeding;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Existing;

[TestClass]
public class DatabaseSeederTests
{
    private static ScratchDatabase? _database;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        _database = await ScratchDatabase.CreateAsync();

        await using var dbContext = CreateContext();

        await dbContext.Database.MigrateAsync();
        await DatabaseSeeder.SeedAsync(dbContext);
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_database != null)
        {
            await _database.DisposeAsync();
        }
    }

    private static ClubDbContext CreateContext()
    {
        return _database!.CreateContext();
    }

    [TestMethod]
    public async Task Seed_CreatesUsersAndAdmins()
    {
        await using var dbContext = CreateContext();

        Assert.AreEqual(200, await dbContext.Users.CountAsync(x => x.Role == "user"));
        Assert.AreEqual(5, await dbContext.Users.CountAsync(x => x.Role == "admin"));
        Assert.IsTrue(await dbContext.Users.AnyAsync(x => x.Email == "user200@club.test"));
        Assert.IsTrue(await dbContext.Users.AnyAsync(x => x.Email == "admin005@club.test"));
    }

    [TestMethod]
    public async Task Seed_CreatesRoomsAndClasses()
    {
        await using var dbContext = CreateContext();

        Assert.AreEqual(10, await dbContext.Rooms.CountAsync());
        Assert.AreEqual(200, await dbContext.Classes.IgnoreQueryFilters().CountAsync());
        Assert.IsTrue(await dbContext.Classes.IgnoreQueryFilters().AnyAsync(x => x.IsCancelled));
    }

    [TestMethod]
    public async Task Seed_GivesEveryUserExactlyOnePass()
    {
        await using var dbContext = CreateContext();

        var userIds = await dbContext.Users.Select(x => x.Id).ToListAsync();
        var passCounts = await dbContext.Passes
            .GroupBy(x => x.UserId)
            .Select(x => new { UserId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count);

        CollectionAssert.AreEquivalent(userIds, passCounts.Keys.ToList());
        Assert.IsTrue(passCounts.Values.All(x => x == 1));
    }

    [TestMethod]
    public async Task Seed_NeverLeavesNegativeVisits()
    {
        await using var dbContext = CreateContext();

        Assert.IsFalse(await dbContext.Passes.AnyAsync(x => x.RemainingVisits < 0));
    }

    [TestMethod]
    public async Task Seed_ActiveSeatsNeverExceedCapacity()
    {
        await using var dbContext = CreateContext();

        var overbooked = await dbContext.Classes
            .IgnoreQueryFilters()
            .Where(x => x.Enrollments.Where(e => e.Status == "active").Sum(e => e.Seats) > x.Capacity)
            .Select(x => x.Id)
            .ToListAsync();

        Assert.AreEqual(0, overbooked.Count);
    }

    [TestMethod]
    public async Task Seed_EnrollmentsAreConsistent()
    {
        await using var dbContext = CreateContext();

        var enrollments = await dbContext.Enrollments.ToListAsync();

        Assert.IsTrue(enrollments.Count > 0);
        Assert.IsTrue(enrollments.All(x => x.Seats >= 1));
        Assert.IsTrue(enrollments.All(x => x.Status is "active" or "cancelled"));
        Assert.IsTrue(enrollments.Where(x => x.Status == "active").All(x => x.CancelledAt == null));
        Assert.IsTrue(enrollments.Where(x => x.Status == "cancelled").All(x => x.CancelledAt != null));
    }

    [TestMethod]
    public async Task Seed_ContainsFullFutureClasses()
    {
        await using var dbContext = CreateContext();

        var now = DateTime.UtcNow;

        var fullClasses = await dbContext.Classes
            .Where(x => x.StartsAt > now)
            .CountAsync(x => x.Enrollments.Where(e => e.Status == "active").Sum(e => e.Seats) == x.Capacity);

        Assert.IsTrue(fullClasses > 0);
    }

    [TestMethod]
    public async Task Seed_ContainsClassesStartingWithinTwoHours()
    {
        await using var dbContext = CreateContext();

        var now = DateTime.UtcNow;

        Assert.IsTrue(await dbContext.Classes.AnyAsync(x => x.StartsAt > now && x.StartsAt < now.AddHours(2)));
    }

    [TestMethod]
    public async Task SeedAgain_IsNoOp()
    {
        await using var dbContext = CreateContext();

        var before = await SnapshotAsync(dbContext);

        await DatabaseSeeder.SeedAsync(dbContext);

        CollectionAssert.AreEqual(before, await SnapshotAsync(dbContext));
    }

    private static async Task<long[]> SnapshotAsync(ClubDbContext dbContext)
    {
        var classes = dbContext.Classes.IgnoreQueryFilters();

        return
        [
            await dbContext.Users.CountAsync(), await dbContext.Users.MaxAsync(x => x.Id),
            await dbContext.Rooms.CountAsync(), await dbContext.Rooms.MaxAsync(x => x.Id),
            await classes.CountAsync(), await classes.MaxAsync(x => x.Id),
            await dbContext.Passes.CountAsync(), await dbContext.Passes.MaxAsync(x => x.Id),
            await dbContext.Enrollments.CountAsync(), await dbContext.Enrollments.MaxAsync(x => x.Id)
        ];
    }
}
