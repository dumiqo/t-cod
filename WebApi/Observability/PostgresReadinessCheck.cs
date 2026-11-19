using DataAccessLayer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace WebApi.Observability;

public sealed class PostgresReadinessCheck : IHealthCheck
{
    private readonly ClubDbContext _dbContext;

    public PostgresReadinessCheck(ClubDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // решение: своё соединение с тайм-аутами Npgsql — подключение к молчащему серверу Npgsql по токену
        // не прерывает (проверено), а брошенное по WaitAsync соединение контекста роняет освобождение scope.
        var timeout = (int)ReadinessChecks.CheckTimeout.TotalSeconds;
        var connectionString = new NpgsqlConnectionStringBuilder(_dbContext.Database.GetConnectionString())
        {
            Timeout = timeout,
            CommandTimeout = timeout
        };

        try
        {
            await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy();
        }
        // решение: исключение возвращается как результат — брошенное встроенный лог пишет на Error
        // при любом failureStatus; так уровень лога следует статусу, а причина остаётся в записи.
        catch (Exception exception)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: exception);
        }
    }
}
