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
