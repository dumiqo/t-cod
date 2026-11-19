using System.Data;
using System.Globalization;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DataAccessLayer.Transactions;

public sealed class TransactionRunner
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly TransactionSettings _settings;

    public TransactionRunner(IUnitOfWork unitOfWork, IOptions<TransactionSettings> settings)
    {
        _unitOfWork = unitOfWork;
        _settings = settings.Value;
    }

    public async Task<T> RunAsync<T>(
        string operation,
        Func<CancellationToken, Task<TransactionResult<T>>> action,
        CancellationToken ct = default)
    {
        var database = _unitOfWork.DbContext.Database;
        var lockTimeout = ((long)_settings.LockTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

        await using var transaction = await database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            // решение: set_config(..., true) — то же, что SET LOCAL, но значение передаётся параметром.
            await database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {lockTimeout}, true)", ct);

            var result = await action(ct);

            if (result.Commit)
            {
                await transaction.CommitAsync(ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
            }

            return result.Value;
        }
        catch (Exception exception) when (ConflictReason(exception) is { } reason)
        {
            throw new TransactionConflictException(operation, reason, exception);
        }
    }

    private static string? ConflictReason(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState switch
                {
                    PostgresErrorCodes.LockNotAvailable => TransactionConflictException.LockTimeout,
                    PostgresErrorCodes.DeadlockDetected => TransactionConflictException.Deadlock,
                    _ => null
                };
            }
        }

        return null;
    }
}
