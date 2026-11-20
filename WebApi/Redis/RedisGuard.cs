using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebApi.Observability;

namespace WebApi.Redis;

public sealed class RedisGuard
{
    private readonly TimeSpan _timeout;
    private readonly ClubMetrics _metrics;
    private readonly ILogger<RedisGuard> _logger;

    public RedisGuard(IOptions<RedisSettings> settings, ClubMetrics metrics, ILogger<RedisGuard> logger)
    {
        _timeout = settings.Value.OperationTimeout;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<bool> TryAsync(string operation, Func<Task> call, CancellationToken ct)
    {
        try
        {
            await call().WaitAsync(_timeout, ct);

            return true;
        }
        catch (Exception exception) when (exception is RedisConnectionException or TimeoutException)
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
