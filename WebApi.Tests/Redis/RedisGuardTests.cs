using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using WebApi.Observability;
using WebApi.Redis;

namespace WebApi.Tests.Redis;

[TestClass]
public class RedisGuardTests
{
    private ServiceProvider _services = null!;
    private FakeLogger<RedisGuard> _logger = null!;
    private MetricCollector<long> _failures = null!;
    private RedisGuard _guard = null!;

    [TestInitialize]
    public void Initialize()
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meters = _services.GetRequiredService<IMeterFactory>();
        _failures = new MetricCollector<long>(meters, ClubMetrics.MeterName, "club.redis.failures");
        _logger = new FakeLogger<RedisGuard>();
        _guard = new RedisGuard(Options.Create(new RedisSettings()), new ClubMetrics(meters), TimeProvider.System, _logger);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _failures.Dispose();
        _services.Dispose();
    }

    [TestMethod]
    public async Task TryAsync_CommandCancelledByRedisClient_ReturnsFalseAndReportsFailure()
    {
        var result = await _guard.TryAsync("rate-limit", () => Task.FromCanceled(new CancellationToken(true)), CancellationToken.None);

        Assert.IsFalse(result);
        var record = _logger.Collector.GetSnapshot().Single();
        Assert.AreEqual(1005, record.Id.Id);
        Assert.AreEqual("Redis failure: rate-limit, TaskCanceledException", record.Message);
        var measurement = _failures.GetMeasurementSnapshot().Single();
        Assert.AreEqual(1, measurement.Value);
        Assert.AreEqual("rate-limit", measurement.Tags["operation"]);
    }

    [TestMethod]
    public async Task TryAsync_RequestAborted_ThrowsWithoutFailure()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() =>
            _guard.TryAsync("rate-limit", () => Task.FromCanceled(aborted.Token), aborted.Token));

        Assert.AreEqual(0, _logger.Collector.Count);
        Assert.AreEqual(0, _failures.GetMeasurementSnapshot().Count);
    }
}
