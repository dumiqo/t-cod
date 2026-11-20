using System.Text.Json;
using DataAccessLayer.Repositories;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace WebApi.CardCache;

public sealed class CardCache : ICardCache
{
    private const string FillScript = """
        local v = redis.call('HGET', KEYS[1], 'v')
        local n = tonumber(ARGV[1])
        if v and tonumber(v) > n then return 0 end
        if not v or tonumber(v) < n then redis.call('DEL', KEYS[1]) end
        redis.call('HSET', KEYS[1], 'v', ARGV[1], ARGV[2], ARGV[3])
        redis.call('PEXPIRE', KEYS[1], ARGV[4])
        return 1
        """;

    private const string TombstoneScript = """
        local v = redis.call('HGET', KEYS[1], 'v')
        if v and tonumber(v) >= tonumber(ARGV[1]) then return 0 end
        redis.call('DEL', KEYS[1])
        redis.call('HSET', KEYS[1], 'v', ARGV[1])
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly long _ttlMilliseconds;

    public CardCache(IConnectionMultiplexer redis, IOptions<CardCacheSettings> settings)
    {
        _redis = redis;
        _ttlMilliseconds = (long)settings.Value.CardTtl.TotalMilliseconds;
    }

    public async Task<EnrollmentCard?> GetAsync(long userId, long id)
    {
        var value = await _redis.GetDatabase().HashGetAsync(Key(userId), id);

        return value.IsNull ? null : JsonSerializer.Deserialize<EnrollmentCard>(value.ToString());
    }

    public Task FillAsync(long userId, VersionedCard card)
    {
        return _redis.GetDatabase().ScriptEvaluateAsync(
            FillScript,
            [Key(userId)],
            [card.Version, card.Card.Id, JsonSerializer.Serialize(card.Card), _ttlMilliseconds]);
    }

    public Task InvalidateAsync(long userId, long version)
    {
        return _redis.GetDatabase().ScriptEvaluateAsync(TombstoneScript, [Key(userId)], [version, _ttlMilliseconds]);
    }

    private static RedisKey Key(long userId)
    {
        return $"card:{userId}";
    }
}
