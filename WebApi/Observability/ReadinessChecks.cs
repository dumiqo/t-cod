using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WebApi.Observability;

public static class ReadinessChecks
{
    public const string Tag = "ready";

    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(3);

    public static IHealthChecksBuilder AddClubReadinessChecks(this IHealthChecksBuilder builder)
    {
        return builder
            .AddCheck<PostgresReadinessCheck>("postgres", HealthStatus.Unhealthy, [Tag], CheckTimeout)
            .AddCheck<RedisReadinessCheck>("redis", HealthStatus.Degraded, [Tag], CheckTimeout);
    }
}
