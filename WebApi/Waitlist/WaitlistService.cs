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

            // решение: абонемент при постановке не проверяется (задача 19): посещения списываются при продвижении.
            var entry = await _unitOfWork.Waitlist.CreateAsync(
                new WaitlistEntry
                {
                    UserId = userId,
                    ClassId = classId,
                    Seats = seats,
                    Status = WaitlistStatuses.Waiting,
                    CreatedAt = _clock.GetUtcNow().UtcDateTime.TruncateToMicroseconds()
                },
                token);

            await _unitOfWork.SaveChangesAsync(token);

            var position = await _unitOfWork.Waitlist.GetPositionAsync(classId, entry.Id, token);

            return TransactionResult.Commit<JoinWaitlistResult>(new JoinWaitlistResult.Created(entry, position));
        }, ct);
    }
}
