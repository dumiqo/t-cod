using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace WebApi.Observability;

public sealed class RedisReadinessCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _connection;

    public RedisReadinessCheck(IConnectionMultiplexer connection)
    {
        _connection = connection;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _connection.GetDatabase().PingAsync().WaitAsync(cancellationToken);

            return HealthCheckResult.Healthy();
        }
        // решение: исключение возвращается как результат — брошенное встроенный лог пишет на Error
        // при любом failureStatus, а падение Redis — Degraded и должно идти на Warning.
        catch (Exception exception)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: exception);
        }
    }
}
