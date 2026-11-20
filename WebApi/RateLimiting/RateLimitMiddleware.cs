using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using StackExchange.Redis;
using WebApi.Observability;
using WebApi.Redis;

namespace WebApi.RateLimiting;

public sealed class RateLimitMiddleware
{
    // решение: 60 запросов и минута — контракт, а не параметр эксплуатации (задача 27).
    private const int Limit = 60;
    private const long WindowMilliseconds = 60_000;

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
    private readonly TimeProvider _clock;
    private readonly ClubMetrics _metrics;
    private readonly ProblemDetailsFactory _problems;
    private readonly ILogger<RateLimitMiddleware> _logger;

    public RateLimitMiddleware(
        RequestDelegate next,
        IConnectionMultiplexer redis,
        RedisGuard guard,
        TimeProvider clock,
        ClubMetrics metrics,
        ProblemDetailsFactory problems,
        ILogger<RateLimitMiddleware> logger)
    {
        _next = next;
        _redis = redis;
        _guard = guard;
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
        var oldest = -1L;

        // решение: Redis недоступен — запрос пропускается и в журнал не попадает (задача 24).
        if (!await _guard.TryAsync(
                "rate-limit",
                async () => oldest = (long)await _redis.GetDatabase().ScriptEvaluateAsync(
                    Script,
                    [$"rate:{userId}"],
                    [now, WindowMilliseconds, Limit, $"{now}:{Guid.NewGuid():N}"]),
                context.RequestAborted)
            || oldest < 0)
        {
            await _next(context);
            return;
        }

        var retryAfter = (oldest + WindowMilliseconds - now + 999) / 1000;
        Log.RequestRateLimited(_logger, userId, retryAfter);
        _metrics.RequestRejected("rate-limit", "too-many-requests");

        context.Response.Headers.RetryAfter = retryAfter.ToString();
        await Results.Problem(_problems.CreateProblemDetails(context, StatusCodes.Status429TooManyRequests)).ExecuteAsync(context);
    }
}
