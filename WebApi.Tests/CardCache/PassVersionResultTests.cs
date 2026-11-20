using Microsoft.Extensions.DependencyInjection;
using WebApi.Enrollments;
using WebApi.Tests.Infrastructure;
using static WebApi.Tests.Waitlist.WaitlistApi;

namespace WebApi.Tests.CardCache;

[TestClass]
public class PassVersionResultTests
{
    [TestMethod]
    public async Task EnrollAsync_Created_CarriesAuthorVersion()
    {
        await using var app = await TestApp.StartAsync();
        var user = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync();
        await app.Data.EnrollmentAsync(user.Id, await app.Data.ClassAsync(), 1);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EnrollmentService>();

        var result = await service.EnrollAsync(user.Id, classId, 1, "key-1", CancellationToken.None);

        var created = result as EnrollResult.Created;
        Assert.IsNotNull(created);
        Assert.AreEqual($"{user.Id}=1", Format(created.Versions));
    }

    [TestMethod]
    public async Task CancelAsync_WithPromotion_CarriesAuthorAndPromotedVersionsOnly()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var waiter = await app.Data.UserAsync();
        var exhausted = await app.Data.UserAsync(remainingVisits: 0);
        var classId = await app.Data.ClassAsync(capacity: 2);
        var id = await app.Data.EnrollmentAsync(owner.Id, classId, 2);
        await EntryAsync(app, exhausted.Id, classId, 1);
        await EntryAsync(app, waiter.Id, classId, 2);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EnrollmentService>();

        var result = await service.CancelAsync(owner.Id, id, CancellationToken.None);

        var cancelled = result as CancelResult.Cancelled;
        Assert.IsNotNull(cancelled);
        Assert.AreEqual($"{owner.Id}=1,{waiter.Id}=1", Format(cancelled.Versions));
    }

    [TestMethod]
    public async Task CancelAsync_Repeated_CarriesNoVersions()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 2, cancelled: true);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EnrollmentService>();

        var result = await service.CancelAsync(owner.Id, id, CancellationToken.None);

        var cancelled = result as CancelResult.Cancelled;
        Assert.IsNotNull(cancelled);
        Assert.IsTrue(cancelled.Repeated);
        Assert.AreEqual(string.Empty, Format(cancelled.Versions));
    }

    [TestMethod]
    public async Task ChangeSeatsAsync_DecreaseWithPromotion_CarriesAuthorAndPromotedVersions()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var waiter = await app.Data.UserAsync();
        var classId = await app.Data.ClassAsync(capacity: 3);
        var id = await app.Data.EnrollmentAsync(owner.Id, classId, 3);
        await EntryAsync(app, waiter.Id, classId, 2);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EnrollmentService>();

        var result = await service.ChangeSeatsAsync(owner.Id, id, 1, CancellationToken.None);

        var changed = result as ChangeSeatsResult.Changed;
        Assert.IsNotNull(changed);
        Assert.AreEqual($"{owner.Id}=1,{waiter.Id}=1", Format(changed.Versions));
    }

    [TestMethod]
    public async Task ChangeSeatsAsync_SameSeats_CarriesNoVersions()
    {
        await using var app = await TestApp.StartAsync();
        var owner = await app.Data.UserAsync();
        var id = await app.Data.EnrollmentAsync(owner.Id, await app.Data.ClassAsync(), 3);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EnrollmentService>();

        var result = await service.ChangeSeatsAsync(owner.Id, id, 3, CancellationToken.None);

        var changed = result as ChangeSeatsResult.Changed;
        Assert.IsNotNull(changed);
        Assert.AreEqual(string.Empty, Format(changed.Versions));
    }

    private static string Format(IReadOnlyDictionary<long, long> versions)
    {
        return string.Join(",", versions.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"));
    }
}
