using System.Collections.Concurrent;
using System.Text.Json;
using DataAccessLayer.Repositories;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebApi.Redis;

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
    private readonly RedisGuard _guard;
    private readonly long _ttlMilliseconds;
    private readonly ConcurrentDictionary<long, long> _pending = new();

    public CardCache(IConnectionMultiplexer redis, RedisGuard guard, IOptions<CardCacheSettings> settings)
    {
        _redis = redis;
        _guard = guard;
        _ttlMilliseconds = (long)settings.Value.CardTtl.TotalMilliseconds;
    }

    public async Task<(bool Available, EnrollmentCard? Card)> GetAsync(long userId, long id, CancellationToken ct)
    {
        var value = RedisValue.Null;

        if (!await RetryPendingAsync(ct)
            || _pending.ContainsKey(userId)
            || !await _guard.TryAsync("cache-get", async () => value = await _redis.GetDatabase().HashGetAsync(Key(userId), id), ct))
        {
            return (false, null);
        }

        return (true, value.IsNull ? null : JsonSerializer.Deserialize<EnrollmentCard>(value.ToString()));
    }

    public async Task FillAsync(long userId, VersionedCard card, CancellationToken ct)
    {
        if (!await RetryPendingAsync(ct) || _pending.ContainsKey(userId))
        {
            return;
        }

        await _guard.TryAsync(
            "cache-set",
            () => _redis.GetDatabase().ScriptEvaluateAsync(
                FillScript,
                [Key(userId)],
                [card.Version, card.Card.Id, JsonSerializer.Serialize(card.Card), _ttlMilliseconds]),
            ct);
    }

    // решение: надгробие пишется после коммита без токена запроса: отмена клиентом потеряла бы сброс,
    // не запомнив его; ожидание ограничено Redis:OperationTimeout.
    public async Task InvalidateAsync(long userId, long version)
    {
        if (!await RetryPendingAsync(CancellationToken.None) || !await TombstoneAsync(userId, version, CancellationToken.None))
        {
            _pending.AddOrUpdate(userId, version, (_, old) => Math.Max(old, version));
        }
    }

    private async Task<bool> RetryPendingAsync(CancellationToken ct)
    {
        foreach (var (userId, version) in _pending)
        {
            if (!await TombstoneAsync(userId, version, ct))
            {
                return false;
            }

            _pending.TryRemove(new KeyValuePair<long, long>(userId, version));
        }

        return true;
    }

    private Task<bool> TombstoneAsync(long userId, long version, CancellationToken ct)
    {
        return _guard.TryAsync(
            "cache-remove",
            () => _redis.GetDatabase().ScriptEvaluateAsync(TombstoneScript, [Key(userId)], [version, _ttlMilliseconds]),
            ct);
    }

    private static RedisKey Key(long userId)
    {
        return $"card:{userId}";
    }
}
