using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.Transactions;
using DataAccessLayer.UnitOfWork;
using WebApi.Idempotency;
using WebApi.Observability;
using WebApi.Time;

namespace WebApi.Enrollments;

public sealed class EnrollmentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly TransactionRunner _transactions;
    private readonly TimeProvider _clock;
    private readonly ILogger<EnrollmentService> _logger;
    private readonly ClubMetrics _metrics;

    public EnrollmentService(
        IUnitOfWork unitOfWork,
        TransactionRunner transactions,
        TimeProvider clock,
        ILogger<EnrollmentService> logger,
        ClubMetrics metrics)
    {
        _unitOfWork = unitOfWork;
        _transactions = transactions;
        _clock = clock;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<EnrollResult> EnrollAsync(long userId, long classId, int seats, string idempotencyKey, CancellationToken ct)
    {
        var result = await EnrollInTransactionAsync(userId, classId, seats, IdempotencyKeyHash.Compute(idempotencyKey), ct);

        // решение: событие и счётчики — после коммита: откат не оставляет в логе списания, которого не было.
        if (result is EnrollResult.Created created)
        {
            Log.EnrollmentCreated(_logger, created.Enrollment.Id, classId, seats, userId, created.RemainingVisits);
            _metrics.EnrollmentCreated(seats);
            _metrics.VisitsCharged(seats);
        }

        if (result is EnrollResult.Replayed replayed)
        {
            Log.IdempotentReplay(_logger, Operations.Enroll, replayed.Response.Id, classId, userId);
            _metrics.IdempotencyReplayed(Operations.Enroll);
        }

        return result;
    }

    public Task<EnrollmentCard?> GetCardAsync(long userId, long id, CancellationToken ct)
    {
        return _unitOfWork.Enrollment.GetCardAsync(id, userId, ct);
    }

    private Task<EnrollResult> EnrollInTransactionAsync(long userId, long classId, int seats, byte[] keyHash, CancellationToken ct)
    {
        return _transactions.RunAsync<EnrollResult>(Operations.Enroll, async token =>
        {
            var item = await _unitOfWork.Class.LockIncludingCancelledAsync(classId, token);

            if (item is null)
            {
                return TransactionResult.Rollback<EnrollResult>(new EnrollResult.ClassNotFound());
            }

            var claim = await _unitOfWork.IdempotencyKey.ClaimAsync(
                new IdempotencyKey
                {
                    UserId = userId,
                    Operation = IdempotentOperation.Enroll,
                    KeyHash = keyHash,
                    ClassId = classId,
                    Seats = seats
                },
                token);

            if (!claim.IsNew)
            {
                return TransactionResult.Rollback(Replay(claim.Key, classId, seats));
            }

            if (item.IsCancelled)
            {
                return TransactionResult.Rollback<EnrollResult>(new EnrollResult.Rejected(RejectReasons.ClassCancelled));
            }

            var pass = await _unitOfWork.Pass.LockByUserAsync(userId, token)
                ?? throw new InvalidOperationException($"User {userId} has no pass.");

            // решение: часы читаются после последнего замка: ожидание на замке до lock_timeout
            // пропустило бы начало занятия или конец срока абонемента.
            var now = _clock.GetUtcNow().UtcDateTime;
            var reason = await CheckAsync(item, pass, seats, now, token);

            if (reason is not null)
            {
                return TransactionResult.Rollback<EnrollResult>(new EnrollResult.Rejected(reason));
            }

            pass.RemainingVisits -= seats;

            var enrollment = await _unitOfWork.Enrollment.CreateAsync(
                new Enrollment
                {
                    UserId = userId,
                    ClassId = classId,
                    Seats = seats,
                    Status = EnrollmentStatuses.Active,
                    CreatedAt = now.TruncateToMicroseconds()
                },
                token);

            await _unitOfWork.SaveChangesAsync(token);

            claim.Key.ResultId = enrollment.Id;
            claim.Key.ResultCreatedAt = enrollment.CreatedAt;
            await _unitOfWork.SaveChangesAsync(token);

            return TransactionResult.Commit<EnrollResult>(new EnrollResult.Created(enrollment, pass.RemainingVisits));
        }, ct);
    }

    private static EnrollResult Replay(IdempotencyKey key, long classId, int seats)
    {
        return key.ClassId == classId && key.Seats == seats
            ? new EnrollResult.Replayed(EnrollmentResponse.Replay(key))
            : new EnrollResult.Rejected(RejectReasons.IdempotencyKeyReused);
    }

    private async Task<string?> CheckAsync(Class item, Pass pass, int seats, DateTime now, CancellationToken ct)
    {
        if (now >= item.StartsAt)
        {
            return RejectReasons.ClassStarted;
        }

        if (pass.ValidUntil < DateOnly.FromDateTime(now))
        {
            return RejectReasons.PassExpired;
        }

        if (pass.RemainingVisits < seats)
        {
            return RejectReasons.PassExhausted;
        }

        var occupied = await _unitOfWork.Enrollment.SumActiveSeatsAsync(item.Id, ct);

        return occupied + seats > item.Capacity ? RejectReasons.CapacityExceeded : null;
    }
}
