using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebApi.Observability;

namespace WebApi.Redis;

public sealed class RedisGuard
{
    // решение: после таймаута операция 5 с не ходит в Redis — зависший Redis стоит одного ожидания на операцию,
    // а восстановление замечается не позже 5 с (критерий 29.4 — 10 с).
    private static readonly long SkipTicks = TimeSpan.FromSeconds(5).Ticks;

    private readonly TimeSpan _timeout;
    private readonly ClubMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly ILogger<RedisGuard> _logger;
    private readonly ConcurrentDictionary<string, StrongBox<long>> _skipUntil = new();

    public RedisGuard(IOptions<RedisSettings> settings, ClubMetrics metrics, TimeProvider clock, ILogger<RedisGuard> logger)
    {
        _timeout = settings.Value.OperationTimeout;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
    }

    public async Task<bool> TryAsync(string operation, Func<Task> call, CancellationToken ct)
    {
        var skipUntil = _skipUntil.GetOrAdd(operation, _ => new StrongBox<long>(long.MinValue));
        var seen = Volatile.Read(ref skipUntil.Value);
        var probe = seen != long.MinValue;
        var now = _clock.GetUtcNow().UtcTicks;

        if (probe && (now < seen || Interlocked.CompareExchange(ref skipUntil.Value, now + SkipTicks, seen) != seen))
        {
            return false;
        }

        try
        {
            await call().WaitAsync(_timeout, ct);

            if (probe)
            {
                Volatile.Write(ref skipUntil.Value, long.MinValue);
            }

            return true;
        }
        // решение: окно открывает только таймаут — быстрый отказ соединения ничего не стоит, дорого лишь ожидание.
        catch (TimeoutException exception)
        {
            Volatile.Write(ref skipUntil.Value, _clock.GetUtcNow().UtcTicks + SkipTicks);
            Fail(operation, exception, exception.GetType().Name);

            return false;
        }
        // решение: StackExchange.Redis при зависшем Redis отменяет ждущие команды сам — это отказ Redis, а не 500 (найдено в 29.4); отмену запроса клиентом пробрасываем.
        catch (Exception exception) when (exception is RedisConnectionException
                                          || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Fail(operation, exception, exception.GetType().Name);

            return false;
        }
    }

    public void ReportStart(IConnectionMultiplexer connection)
    {
        if (!connection.IsConnected)
        {
            Fail("connect", null, "not connected after start");
        }
    }

    private void Fail(string operation, Exception? exception, string reason)
    {
        Log.RedisFailure(_logger, exception, operation, reason);
        _metrics.RedisFailed(operation);
    }
}
