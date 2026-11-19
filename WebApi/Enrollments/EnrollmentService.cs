using DataAccessLayer.Entities;
using DataAccessLayer.Transactions;
using DataAccessLayer.UnitOfWork;
using WebApi.Time;

namespace WebApi.Enrollments;

public sealed class EnrollmentService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly TransactionRunner _transactions;
    private readonly TimeProvider _clock;

    public EnrollmentService(IUnitOfWork unitOfWork, TransactionRunner transactions, TimeProvider clock)
    {
        _unitOfWork = unitOfWork;
        _transactions = transactions;
        _clock = clock;
    }

    public Task<EnrollResult> EnrollAsync(long userId, long classId, int seats, CancellationToken ct)
    {
        return _transactions.RunAsync<EnrollResult>(Operations.Enroll, async token =>
        {
            var item = await _unitOfWork.Class.LockIncludingCancelledAsync(classId, token);

            if (item is null)
            {
                return TransactionResult.Rollback<EnrollResult>(new EnrollResult.ClassNotFound());
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

            return TransactionResult.Commit<EnrollResult>(new EnrollResult.Created(enrollment));
        }, ct);
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
