using Common.Caching;
using StackExchange.Redis;

namespace WebApi.Tests.Existing;

[TestClass]
public class RedisCacheServiceTests
{
    private static ConnectionMultiplexer _connection = null!;

    private RedisCacheService _cache = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        _connection = await ConnectionMultiplexer.ConnectAsync(TestDatabase.RedisConnectionString);
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        await _connection.DisposeAsync();
    }

    [TestInitialize]
    public void Initialize()
    {
        _cache = new RedisCacheService(_connection, TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task SetThenGet_ReturnsStoredValue()
    {
        var key = NewKey();
        var value = new Sample(42, "Yoga", new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc));

        await _cache.SetAsync(key, value);

        Assert.AreEqual(value, await _cache.GetAsync<Sample>(key));
    }

    [TestMethod]
    public async Task Get_MissingKey_ReturnsDefault()
    {
        Assert.IsNull(await _cache.GetAsync<Sample>(NewKey()));
    }

    [TestMethod]
    public async Task Remove_DeletesValue()
    {
        var key = NewKey();

        await _cache.SetAsync(key, new Sample(1, "Pilates", DateTime.UtcNow));
        await _cache.RemoveAsync(key);

        Assert.IsNull(await _cache.GetAsync<Sample>(key));
    }

    [TestMethod]
    public async Task Set_WithoutTtl_AppliesDefaultTtl()
    {
        var key = NewKey();

        await _cache.SetAsync(key, new Sample(1, "Boxing", DateTime.UtcNow));

        var ttl = await _connection.GetDatabase().KeyTimeToLiveAsync(key);

        Assert.IsNotNull(ttl);
        Assert.IsTrue(ttl > TimeSpan.FromMinutes(4) && ttl <= TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public async Task Set_WithTtl_AppliesGivenTtl()
    {
        var key = NewKey();

        await _cache.SetAsync(key, new Sample(1, "TRX", DateTime.UtcNow), TimeSpan.FromSeconds(30));

        var ttl = await _connection.GetDatabase().KeyTimeToLiveAsync(key);

        Assert.IsNotNull(ttl);
        Assert.IsTrue(ttl > TimeSpan.FromSeconds(20) && ttl <= TimeSpan.FromSeconds(30));
    }

    private static string NewKey()
    {
        return $"tests:{Guid.NewGuid():N}";
    }

    private record Sample(long Id, string Title, DateTime At);
}
