using DataAccessLayer.Entities;
using DataAccessLayer.Transactions;
using DataAccessLayer.UnitOfWork;
using WebApi.Enrollments;
using WebApi.Idempotency;
using WebApi.Observability;
using WebApi.Time;

namespace WebApi.Waitlist;

public sealed class WaitlistService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly TransactionRunner _transactions;
    private readonly TimeProvider _clock;
    private readonly ILogger<WaitlistService> _logger;
    private readonly ClubMetrics _metrics;

    public WaitlistService(
        IUnitOfWork unitOfWork,
        TransactionRunner transactions,
        TimeProvider clock,
        ILogger<WaitlistService> logger,
        ClubMetrics metrics)
    {
        _unitOfWork = unitOfWork;
        _transactions = transactions;
        _clock = clock;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<JoinWaitlistResult> JoinAsync(long userId, long classId, int seats, string idempotencyKey, CancellationToken ct)
    {
        var result = await JoinInTransactionAsync(userId, classId, seats, IdempotencyKeyHash.Compute(idempotencyKey), ct);

        if (result is JoinWaitlistResult.Created created)
        {
            Log.WaitlistJoined(_logger, created.Entry.Id, classId, seats, userId, created.Position);
            _metrics.WaitlistJoined(seats);
        }

        if (result is JoinWaitlistResult.Replayed replayed)
        {
            Log.IdempotentReplay(_logger, Operations.JoinWaitlist, replayed.Response.Id, classId, userId);
            _metrics.IdempotencyReplayed(Operations.JoinWaitlist);
        }

        return result;
    }

    private Task<JoinWaitlistResult> JoinInTransactionAsync(long userId, long classId, int seats, byte[] keyHash, CancellationToken ct)
    {
        return _transactions.RunAsync<JoinWaitlistResult>(Operations.JoinWaitlist, async token =>
        {
            var item = await _unitOfWork.Class.LockIncludingCancelledAsync(classId, token);

            if (item is null)
            {
                return TransactionResult.Rollback<JoinWaitlistResult>(new JoinWaitlistResult.ClassNotFound());
            }

            var claim = await _unitOfWork.IdempotencyKey.ClaimAsync(
                new IdempotencyKey
                {
                    UserId = userId,
                    Operation = IdempotentOperation.JoinWaitlist,
                    KeyHash = keyHash,
                    ClassId = classId,
                    Seats = seats
                },
                token);

            if (!claim.IsNew)
            {
                return TransactionResult.Rollback<JoinWaitlistResult>(
                    claim.Key.ClassId == classId && claim.Key.Seats == seats
                        ? new JoinWaitlistResult.Replayed(JoinWaitlistResponse.Replay(claim.Key))
                        : new JoinWaitlistResult.Rejected(RejectReasons.IdempotencyKeyReused));
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

            claim.Key.ResultId = entry.Id;
            claim.Key.ResultCreatedAt = entry.CreatedAt;
            claim.Key.ResultPosition = position;
            await _unitOfWork.SaveChangesAsync(token);

            return TransactionResult.Commit<JoinWaitlistResult>(new JoinWaitlistResult.Created(entry, position));
        }, ct);
    }

    public async Task<LeaveWaitlistResult> LeaveAsync(long userId, long id, CancellationToken ct)
    {
        var found = await _unitOfWork.Waitlist.FindOwnedAsync(id, userId, ct);

        if (found is null)
        {
            return new LeaveWaitlistResult.NotFound();
        }

        // решение: cancelled и promoted — конечные статусы, ответ на них без транзакции и замка.
        if (found.Status != WaitlistStatuses.Waiting)
        {
            return Settled(found);
        }

        WaitlistEntry? left = null;

        var result = await _transactions.RunAsync<LeaveWaitlistResult>(Operations.LeaveWaitlist, async token =>
        {
            _ = await _unitOfWork.Class.LockIncludingCancelledAsync(found.ClassId, token)
                ?? throw new InvalidOperationException($"Class {found.ClassId} of waitlist entry {id} not found.");

            var entry = await _unitOfWork.Waitlist.GetByIdAsync(id, token)
                ?? throw new InvalidOperationException($"Waitlist entry {id} disappeared.");

            if (entry.Status != WaitlistStatuses.Waiting)
            {
                return TransactionResult.Rollback(Settled(entry));
            }

            // решение: уход с начавшегося и с отменённого занятия разрешён (задача 19): запись очереди ничего не занимает.
            entry.Status = WaitlistStatuses.Cancelled;
            entry.CancelledAt = _clock.GetUtcNow().UtcDateTime.TruncateToMicroseconds();

            await _unitOfWork.SaveChangesAsync(token);
            left = entry;

            return TransactionResult.Commit<LeaveWaitlistResult>(new LeaveWaitlistResult.Left(entry.Id));
        }, ct);

        if (left is not null)
        {
            Log.WaitlistLeft(_logger, left.Id, left.ClassId, left.Seats, userId);
            _metrics.WaitlistLeft(left.Seats);
        }

        return result;
    }

    private static LeaveWaitlistResult Settled(WaitlistEntry entry)
    {
        // решение: уход после продвижения — 409 already-promoted (задача 19): бронь отменяется через §3.
        return entry.Status == WaitlistStatuses.Promoted
            ? new LeaveWaitlistResult.Rejected(RejectReasons.AlreadyPromoted)
            : new LeaveWaitlistResult.Left(entry.Id);
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
