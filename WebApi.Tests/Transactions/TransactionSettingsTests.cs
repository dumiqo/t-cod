using DataAccessLayer.Transactions;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebApi.Tests.Infrastructure;

namespace WebApi.Tests.Transactions;

[TestClass]
public class TransactionSettingsTests
{
    [TestMethod]
    public async Task StartAsync_NoSetting_LockTimeoutIsTenSeconds()
    {
        await using var app = await TestApp.StartAsync();

        var settings = app.Services.GetRequiredService<IOptions<TransactionSettings>>().Value;

        Assert.AreEqual(TimeSpan.FromSeconds(10), settings.LockTimeout);
    }

    [TestMethod]
    public async Task StartAsync_LockTimeoutJustBelowCommandTimeout_Starts()
    {
        await using var app = await StartWithLockTimeoutAsync("00:00:29.999");

        var settings = app.Services.GetRequiredService<IOptions<TransactionSettings>>().Value;

        Assert.AreEqual(TimeSpan.FromMilliseconds(29999), settings.LockTimeout);
    }

    [TestMethod]
    public async Task StartAsync_LockTimeoutOneMillisecond_Starts()
    {
        await using var app = await StartWithLockTimeoutAsync("00:00:00.001");

        var settings = app.Services.GetRequiredService<IOptions<TransactionSettings>>().Value;

        Assert.AreEqual(TimeSpan.FromMilliseconds(1), settings.LockTimeout);
    }

    [TestMethod]
    [DataRow("00:00:30")]
    [DataRow("00:00:00.0009")]
    [DataRow("00:00:00")]
    [DataRow("-00:00:01")]
    public async Task StartAsync_LockTimeoutOutOfRange_FailsWithValidationError(string value)
    {
        var exception = await Assert.ThrowsExceptionAsync<OptionsValidationException>(() => StartWithLockTimeoutAsync(value));

        Assert.AreEqual(typeof(TransactionSettings), exception.OptionsType);
        StringAssert.Contains(exception.Message, "Transactions:LockTimeout");
    }

    [TestMethod]
    public async Task RunAsync_FromHostScope_SharesContextWithUnitOfWork()
    {
        await using var app = await StartWithLockTimeoutAsync("00:00:00.300");
        long passId;
        int before;
        string inside;

        await using (var check = app.CreateContext())
        {
            var pass = await check.Passes.OrderBy(x => x.Id).FirstAsync();
            passId = pass.Id;
            before = pass.RemainingVisits;
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<TransactionRunner>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            inside = await runner.RunAsync("probe-operation", async ct =>
            {
                var tracked = await unitOfWork.Pass.GetByIdAsync(passId, ct);
                tracked!.RemainingVisits += 1;
                await unitOfWork.SaveChangesAsync(ct);
                var setting = await unitOfWork.DbContext.Database
                    .SqlQueryRaw<string>("SELECT current_setting('lock_timeout') AS \"Value\"")
                    .SingleAsync(ct);
                return TransactionResult.Rollback(setting);
            });
        }

        await using var context = app.CreateContext();

        Assert.AreEqual("300ms", inside);
        Assert.AreEqual(before, await context.Passes.Where(x => x.Id == passId).Select(x => x.RemainingVisits).SingleAsync());
    }

    private static Task<TestApp> StartWithLockTimeoutAsync(string value)
    {
        return TestApp.StartAsync(builder => builder.UseSetting("Transactions:LockTimeout", value));
    }
}
