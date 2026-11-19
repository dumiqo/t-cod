using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Enrollments;

[TestClass]
public class ClassRepositoryTests
{
    [TestMethod]
    public async Task FindIncludingCancelledAsync_CancelledClass_ReturnsIt()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var cancelledId = await context.Classes.IgnoreQueryFilters()
            .Where(x => x.IsCancelled)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .FirstAsync();

        var found = await new UnitOfWork(context).Class.FindIncludingCancelledAsync(cancelledId);

        Assert.IsNotNull(found);
        Assert.AreEqual(cancelledId, found.Id);
        Assert.IsTrue(found.IsCancelled);
    }

    [TestMethod]
    public async Task FindIncludingCancelledAsync_ActiveClass_ReturnsIt()
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();
        var active = await context.Classes.AsNoTracking().OrderBy(x => x.Id).FirstAsync();

        var found = await new UnitOfWork(context).Class.FindIncludingCancelledAsync(active.Id);

        Assert.IsNotNull(found);
        Assert.AreEqual(active.Id, found.Id);
        Assert.AreEqual(active.StartsAt, found.StartsAt);
        Assert.AreEqual(active.Capacity, found.Capacity);
        Assert.IsFalse(found.IsCancelled);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(999999999L)]
    public async Task FindIncludingCancelledAsync_MissingClass_ReturnsNull(long id)
    {
        await using var db = await TestDb.CloneAsync();
        await using var context = db.CreateContext();

        var found = await new UnitOfWork(context).Class.FindIncludingCancelledAsync(id);

        Assert.IsNull(found);
    }
}
