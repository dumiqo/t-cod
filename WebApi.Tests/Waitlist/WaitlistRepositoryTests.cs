using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Waitlist;

[TestClass]
public class WaitlistRepositoryTests
{
    [TestMethod]
    public async Task GetPositionAsync_ThreeWaiting_ReturnsOneTwoThree()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await FirstClassAsync(context);
        var first = await AddAsync(context, classId, 0, "waiting");
        var second = await AddAsync(context, classId, 1, "waiting");
        var third = await AddAsync(context, classId, 2, "waiting");
        var waitlist = new UnitOfWork(context).Waitlist;

        CollectionAssert.AreEqual(
            new[] { 1, 2, 3 },
            new[]
            {
                await waitlist.GetPositionAsync(classId, first),
                await waitlist.GetPositionAsync(classId, second),
                await waitlist.GetPositionAsync(classId, third)
            });
    }

    [TestMethod]
    public async Task GetPositionAsync_CancelledAndPromotedAhead_NotCounted()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await FirstClassAsync(context);
        await AddAsync(context, classId, 0, "cancelled");
        await AddAsync(context, classId, 1, "waiting");
        await AddAsync(context, classId, 2, "promoted");
        var own = await AddAsync(context, classId, 3, "waiting");

        var position = await new UnitOfWork(context).Waitlist.GetPositionAsync(classId, own);

        Assert.AreEqual(2, position);
    }

    [TestMethod]
    public async Task GetPositionAsync_OtherClassAheadAndWaitingBehind_NotCounted()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await FirstClassAsync(context);
        var otherId = await context.Classes.Where(x => x.Id > classId).OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        await AddAsync(context, otherId, 0, "waiting");
        await AddAsync(context, otherId, 1, "waiting");
        var own = await AddAsync(context, classId, 2, "waiting");
        await AddAsync(context, classId, 3, "waiting");

        var position = await new UnitOfWork(context).Waitlist.GetPositionAsync(classId, own);

        Assert.AreEqual(1, position);
    }

    [TestMethod]
    public async Task IsWaitingAsync_OwnWaiting_ReturnsTrue()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await FirstClassAsync(context);
        var userId = await UserIdAsync(context, 0);
        await AddAsync(context, classId, 0, "cancelled");
        await AddAsync(context, classId, 0, "waiting");

        Assert.IsTrue(await new UnitOfWork(context).Waitlist.IsWaitingAsync(classId, userId));
    }

    [TestMethod]
    public async Task IsWaitingAsync_OwnCancelledPromotedOtherClassAndOthersWaiting_ReturnsFalse()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await FirstClassAsync(context);
        var otherId = await context.Classes.Where(x => x.Id > classId).OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        var userId = await UserIdAsync(context, 0);
        await AddAsync(context, classId, 0, "cancelled");
        await AddAsync(context, classId, 0, "promoted");
        await AddAsync(context, otherId, 0, "waiting");
        await AddAsync(context, classId, 1, "waiting");

        Assert.IsFalse(await new UnitOfWork(context).Waitlist.IsWaitingAsync(classId, userId));
    }

    [TestMethod]
    public async Task FindOwnedAsync_OwnEntryOnCancelledClass_ReturnsUntrackedEntry()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await FirstClassAsync(context);
        await context.Classes.IgnoreQueryFilters().Where(x => x.Id == classId).ExecuteUpdateAsync(x => x.SetProperty(c => c.IsCancelled, true));
        var userId = await UserIdAsync(context, 0);
        var id = await AddAsync(context, classId, 0, "promoted");
        context.ChangeTracker.Clear();

        var entry = await new UnitOfWork(context).Waitlist.FindOwnedAsync(id, userId);

        Assert.IsNotNull(entry);
        Assert.AreEqual(id, entry.Id);
        Assert.AreEqual(classId, entry.ClassId);
        Assert.AreEqual("promoted", entry.Status);
        Assert.AreEqual(0, context.ChangeTracker.Entries().Count());
    }

    [TestMethod]
    public async Task FindOwnedAsync_MissingAndOthers_ReturnsNull()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await FirstClassAsync(context);
        var otherUserId = await UserIdAsync(context, 1);
        var id = await AddAsync(context, classId, 0, "waiting");
        var waitlist = new UnitOfWork(context).Waitlist;

        Assert.IsNull(await waitlist.FindOwnedAsync(id, otherUserId));
        Assert.IsNull(await waitlist.FindOwnedAsync(id + 1000000, otherUserId));
    }

    [TestMethod]
    public async Task GetWaitingAsync_MixedEntries_ReturnsTrackedWaitingOfClassOrderedById()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var classId = await FirstClassAsync(context);
        var otherId = await context.Classes.Where(x => x.Id > classId).OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
        var first = await AddAsync(context, classId, 0, "waiting");
        await AddAsync(context, classId, 1, "cancelled");
        await AddAsync(context, otherId, 2, "waiting");
        await AddAsync(context, classId, 3, "promoted");
        var second = await AddAsync(context, classId, 4, "waiting");
        context.ChangeTracker.Clear();

        var entries = await new UnitOfWork(context).Waitlist.GetWaitingAsync(classId);

        CollectionAssert.AreEqual(new[] { first, second }, entries.Select(x => x.Id).ToArray());
        Assert.AreEqual(2, context.ChangeTracker.Entries<WaitlistEntry>().Count());
    }

    private static Task<long> UserIdAsync(ClubDbContext context, int userIndex)
    {
        return context.Users.OrderBy(x => x.Id).Skip(userIndex).Select(x => x.Id).FirstAsync();
    }

    private static Task<long> FirstClassAsync(ClubDbContext context)
    {
        return context.Classes.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync();
    }

    private static async Task<long> AddAsync(ClubDbContext context, long classId, int userIndex, string status)
    {
        var userId = await context.Users.OrderBy(x => x.Id).Skip(userIndex).Select(x => x.Id).FirstAsync();
        var entry = new WaitlistEntry
        {
            ClassId = classId,
            UserId = userId,
            Seats = 1,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };

        context.WaitlistEntries.Add(entry);
        await context.SaveChangesAsync();

        return entry.Id;
    }
}
