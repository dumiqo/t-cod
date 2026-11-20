using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using StackExchange.Redis;
using WebApi.Observability;
using WebApi.Redis;

namespace WebApi.RateLimiting;

public sealed class RateLimitMiddleware
{
    private const string Script = """
        local now = tonumber(ARGV[1])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - tonumber(ARGV[2]))
        if redis.call('ZCARD', KEYS[1]) < tonumber(ARGV[3]) then
            redis.call('ZADD', KEYS[1], now, ARGV[4])
            redis.call('PEXPIRE', KEYS[1], ARGV[2])
            return -1
        end
        return tonumber(redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')[2])
        """;

    private readonly RequestDelegate _next;
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisGuard _guard;
    private readonly LocalRateJournal _journal;
    private readonly TimeProvider _clock;
    private readonly ClubMetrics _metrics;
    private readonly ProblemDetailsFactory _problems;
    private readonly ILogger<RateLimitMiddleware> _logger;

    public RateLimitMiddleware(
        RequestDelegate next,
        IConnectionMultiplexer redis,
        RedisGuard guard,
        LocalRateJournal journal,
        TimeProvider clock,
        ClubMetrics metrics,
        ProblemDetailsFactory problems,
        ILogger<RateLimitMiddleware> logger)
    {
        _next = next;
        _redis = redis;
        _guard = guard;
        _journal = journal;
        _clock = clock;
        _metrics = metrics;
        _problems = problems;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || context.GetEndpoint()?.Metadata.GetMetadata<RateLimitedAttribute>() is null)
        {
            await _next(context);
            return;
        }

        var userId = long.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (!_journal.TryReserve(userId, now, out var oldest))
        {
            await RejectAsync(context, userId, now, oldest);
            return;
        }

        oldest = -1L;

        // решение: Redis недоступен — решает локальный журнал, бронь в нём остаётся (задача 28.2).
        if (!await _guard.TryAsync(
                "rate-limit",
                async () => oldest = (long)await _redis.GetDatabase().ScriptEvaluateAsync(
                    Script,
                    [$"rate:{userId}"],
                    [now, RateLimitWindow.Milliseconds, RateLimitWindow.Limit, $"{now}:{Guid.NewGuid():N}"]),
                context.RequestAborted)
            || oldest < 0)
        {
            await _next(context);
            return;
        }

        _journal.Release(userId, now);
        await RejectAsync(context, userId, now, oldest);
    }

    private async Task RejectAsync(HttpContext context, long userId, long now, long oldest)
    {
        var retryAfter = (oldest + RateLimitWindow.Milliseconds - now + 999) / 1000;
        Log.RequestRateLimited(_logger, userId, retryAfter);
        _metrics.RequestRejected("rate-limit", "too-many-requests");

        context.Response.Headers.RetryAfter = retryAfter.ToString();
        await Results.Problem(_problems.CreateProblemDetails(context, StatusCodes.Status429TooManyRequests)).ExecuteAsync(context);
    }
}
