using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using DataAccessLayer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace WebApi.Tests.Infrastructure;

public sealed class TestApp : IAsyncDisposable
{
    private static readonly string[] Instruments =
    [
        "club.enrollments.created",
        "club.visits.charged",
        "club.requests.rejected",
        "club.idempotency.replayed"
    ];

    private readonly TestDb _db;
    private readonly TestAppFactory _factory;
    private readonly Dictionary<string, MetricCollector<long>> _metrics;

    private TestApp(TestDb db, TestAppFactory factory, FakeTimeProvider clock, string redisKeyPrefix)
    {
        _db = db;
        _factory = factory;
        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        _metrics = Instruments.ToDictionary(x => x, x => new MetricCollector<long>(meterFactory, "FitnessClub.Api", x));
        Clock = clock;
        RedisKeyPrefix = redisKeyPrefix;
        Data = new TestData(this);
    }

    public FakeTimeProvider Clock { get; }

    public TestData Data { get; }

    public string RedisKeyPrefix { get; }

    public DateTimeOffset SeededAt => _db.SeededAt;

    public string ConnectionString => _db.ConnectionString;

    public IServiceProvider Services => _factory.Services;

    public IDatabase Redis => Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    public FakeLogCollector Logs => Services.GetRequiredService<FakeLogCollector>();

    public MetricCollector<long> Metrics(string instrument)
    {
        return _metrics.TryGetValue(instrument, out var collector)
            ? collector
            : throw new ArgumentException($"Instrument {instrument} is not collected by TestApp.", nameof(instrument));
    }

    public static async Task<TestApp> StartAsync(Action<IWebHostBuilder>? configure = null)
    {
        var db = await TestDb.CloneAsync();
        // решение: часы стоят на моменте засева и двигаются только тестом — границы времени детерминированы.
        var clock = new FakeTimeProvider(db.SeededAt);
        var redisKeyPrefix = $"test:{Guid.NewGuid():N}:";
        var factory = new TestAppFactory(db.ConnectionString, clock, redisKeyPrefix, configure);

        try
        {
            _ = factory.Server;
        }
        catch
        {
            await factory.DisposeAsync();
            await db.DisposeAsync();
            throw;
        }

        return new TestApp(db, factory, clock, redisKeyPrefix);
    }

    public HttpClient CreateClient()
    {
        return _factory.CreateClient();
    }

    public async Task<HttpClient> CreateClientAsync(string email)
    {
        var client = CreateClient();
        var token = await client.GetTokenAsync(email);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    public ClubDbContext CreateContext()
    {
        return _db.CreateContext();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var collector in _metrics.Values)
        {
            collector.Dispose();
        }

        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }
}
