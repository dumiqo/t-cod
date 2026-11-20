using DataAccessLayer.Entities;
using DataAccessLayer.Transactions;
using DataAccessLayer.UnitOfWork;
using WebApi.Enrollments;
using WebApi.Time;

namespace WebApi.Waitlist;

public sealed class WaitlistService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly TransactionRunner _transactions;
    private readonly TimeProvider _clock;

    public WaitlistService(IUnitOfWork unitOfWork, TransactionRunner transactions, TimeProvider clock)
    {
        _unitOfWork = unitOfWork;
        _transactions = transactions;
        _clock = clock;
    }

    public Task<JoinWaitlistResult> JoinAsync(long userId, long classId, int seats, CancellationToken ct)
    {
        return _transactions.RunAsync<JoinWaitlistResult>(Operations.JoinWaitlist, async token =>
        {
            var item = await _unitOfWork.Class.LockIncludingCancelledAsync(classId, token);

            if (item is null)
            {
                return TransactionResult.Rollback<JoinWaitlistResult>(new JoinWaitlistResult.ClassNotFound());
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            var reason = await CheckAsync(item, userId, seats, now, token);

            if (reason is not null)
            {
                return TransactionResult.Rollback<JoinWaitlistResult>(new JoinWaitlistResult.Rejected(reason));
            }

            // решение: абонемент при постановке не проверяется (задача 19): посещения списываются при продвижении.
            var entry = await _unitOfWork.Waitlist.CreateAsync(
                new WaitlistEntry
                {
                    UserId = userId,
                    ClassId = classId,
                    Seats = seats,
                    Status = WaitlistStatuses.Waiting,
                    CreatedAt = now.TruncateToMicroseconds()
                },
                token);

            await _unitOfWork.SaveChangesAsync(token);

            var position = await _unitOfWork.Waitlist.GetPositionAsync(classId, entry.Id, token);

            return TransactionResult.Commit<JoinWaitlistResult>(new JoinWaitlistResult.Created(entry, position));
        }, ct);
    }

    private async Task<string?> CheckAsync(Class item, long userId, int seats, DateTime now, CancellationToken ct)
    {
        if (item.IsCancelled)
        {
            return RejectReasons.ClassCancelled;
        }

        if (now >= item.StartsAt)
        {
            return RejectReasons.ClassStarted;
        }

        if (await _unitOfWork.Waitlist.IsWaitingAsync(item.Id, userId, ct))
        {
            return RejectReasons.AlreadyWaiting;
        }

        // решение: запрос больше вместимости не отклоняется (задача 19): своей причины в §7 нет, а seats-available неверна.
        var occupied = await _unitOfWork.Enrollment.SumActiveSeatsAsync(item.Id, ct);

        return item.Capacity - occupied >= seats ? RejectReasons.SeatsAvailable : null;
    }
}
