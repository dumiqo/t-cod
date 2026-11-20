using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.Transactions;
using DataAccessLayer.UnitOfWork;
using WebApi.Idempotency;
using WebApi.Observability;
using WebApi.Time;
using WebApi.Waitlist;

namespace WebApi.Enrollments;

public sealed class EnrollmentService
{
    private static readonly TimeSpan RefundWindow = TimeSpan.FromHours(2);

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

    public async Task<CancelResult> CancelAsync(long userId, long id, CancellationToken ct)
    {
        var found = await _unitOfWork.Enrollment.FindOwnedAsync(id, userId, ct);

        if (found is null)
        {
            return new CancelResult.NotFound();
        }

        // решение: отменённая бронь отдаётся без замков и транзакции: cancelled — конечный статус,
        // а refunded_visits пишется в той же транзакции, что и статус.
        if (found.Status == EnrollmentStatuses.Cancelled)
        {
            return Repeat(found);
        }

        var result = await _transactions.RunAsync<CancelResult>(Operations.CancelEnrollment, async token =>
        {
            var item = await _unitOfWork.Class.LockIncludingCancelledAsync(found.ClassId, token)
                ?? throw new InvalidOperationException($"Class {found.ClassId} of enrollment {id} not found.");

            var enrollment = await _unitOfWork.Enrollment.GetByIdAsync(id, token)
                ?? throw new InvalidOperationException($"Enrollment {id} disappeared.");

            if (enrollment.Status == EnrollmentStatuses.Cancelled)
            {
                return TransactionResult.Rollback(Repeat(enrollment));
            }

            // решение: на отменённом клубом занятии очередь не продвигается и не читается (задача 21).
            var queue = item.IsCancelled ? [] : await _unitOfWork.Waitlist.GetWaitingAsync(item.Id, token);
            var passes = await _unitOfWork.Pass.LockByUsersAsync([userId, .. queue.Select(x => x.UserId)], token);
            var pass = passes.SingleOrDefault(x => x.UserId == userId)
                ?? throw new InvalidOperationException($"User {userId} has no pass.");

            var now = _clock.GetUtcNow().UtcDateTime;

            if (!item.IsCancelled && now >= item.StartsAt)
            {
                return TransactionResult.Rollback<CancelResult>(new CancelResult.Rejected(RejectReasons.ClassStarted));
            }

            // решение: правила сверяются по сырому now, а cancelled_at — усечённый now: на тик ближе двух часов
            // возврата нет, даже если cancelled_at в базе равен startsAt − 2h.
            var refundedVisits = item.IsCancelled || item.StartsAt - now >= RefundWindow ? enrollment.Seats : 0;

            enrollment.Status = EnrollmentStatuses.Cancelled;
            enrollment.CancelledAt = now.TruncateToMicroseconds();
            enrollment.RefundedVisits = refundedVisits;
            // решение: переполнение остатка (пополнили мимо API) не проверяется: отрицательную сумму ловит
            // CHECK remaining_visits >= 0, ответ 500 и откат; своя причина 409 сломала бы контракт.
            pass.RemainingVisits += refundedVisits;
            pass.Version++;

            await _unitOfWork.SaveChangesAsync(token);
            var promotions = await PromoteAsync(item, queue, passes, userId, now, token);

            return TransactionResult.Commit<CancelResult>(
                new CancelResult.Cancelled(enrollment.Id, item.Id, enrollment.Seats, refundedVisits, pass.RemainingVisits, false, promotions, Versions(passes, userId, promotions)));
        }, ct);

        if (result is CancelResult.Cancelled { Repeated: false, RemainingVisits: { } remaining } cancelled)
        {
            if (cancelled.RefundedVisits > 0)
            {
                Log.EnrollmentCancelled(_logger, id, cancelled.ClassId, cancelled.Seats, userId, cancelled.RefundedVisits, remaining);
                _metrics.VisitsRefunded(cancelled.RefundedVisits);
            }
            else
            {
                Log.EnrollmentCancelledWithoutRefund(_logger, id, cancelled.ClassId, cancelled.Seats, userId, remaining);
            }

            ObservePromotions(cancelled.ClassId, cancelled.Promotions);
        }

        return result;
    }

    public async Task<ChangeSeatsResult> ChangeSeatsAsync(long userId, long id, int seats, CancellationToken ct)
    {
        var found = await _unitOfWork.Enrollment.FindOwnedAsync(id, userId, ct);

        if (found is null)
        {
            return new ChangeSeatsResult.NotFound();
        }

        // решение: отменённая бронь отклоняется без замков: cancelled — конечный статус, а enrollment-cancelled —
        // первая причина шкалы, так что приоритет не меняется, а запрос не ждёт замок горячего занятия.
        if (found.Status == EnrollmentStatuses.Cancelled)
        {
            return new ChangeSeatsResult.Rejected(RejectReasons.EnrollmentCancelled);
        }

        var result = await _transactions.RunAsync<ChangeSeatsResult>(Operations.ChangeSeats, async token =>
        {
            var item = await _unitOfWork.Class.LockIncludingCancelledAsync(found.ClassId, token)
                ?? throw new InvalidOperationException($"Class {found.ClassId} of enrollment {id} not found.");

            var enrollment = await _unitOfWork.Enrollment.GetByIdAsync(id, token)
                ?? throw new InvalidOperationException($"Enrollment {id} disappeared.");

            if (enrollment.Status == EnrollmentStatuses.Cancelled)
            {
                return TransactionResult.Rollback<ChangeSeatsResult>(new ChangeSeatsResult.Rejected(RejectReasons.EnrollmentCancelled));
            }

            if (item.IsCancelled)
            {
                return TransactionResult.Rollback<ChangeSeatsResult>(new ChangeSeatsResult.Rejected(RejectReasons.ClassCancelled));
            }

            // решение: очередь читается только при уменьшении: то же число мест и увеличение простаивающие места не раздают.
            var queue = seats < enrollment.Seats ? await _unitOfWork.Waitlist.GetWaitingAsync(item.Id, token) : [];
            var passes = await _unitOfWork.Pass.LockByUsersAsync([userId, .. queue.Select(x => x.UserId)], token);
            var pass = passes.SingleOrDefault(x => x.UserId == userId)
                ?? throw new InvalidOperationException($"User {userId} has no pass.");

            var now = _clock.GetUtcNow().UtcDateTime;
            var oldSeats = enrollment.Seats;
            var diff = seats - oldSeats;
            // решение: SumActiveSeatsAsync уже включает свою бронь, поэтому к занятым прибавляется разница, а не новое
            // число мест; абонемент проверяется только при увеличении.
            var reason = diff > 0
                ? await CheckAsync(item, pass, diff, now, token)
                : now >= item.StartsAt ? RejectReasons.ClassStarted : null;

            if (reason is not null)
            {
                return TransactionResult.Rollback<ChangeSeatsResult>(new ChangeSeatsResult.Rejected(reason));
            }

            IReadOnlyList<PromotionOutcome> promotions = [];

            if (seats != oldSeats)
            {
                // решение: уменьшение возвращает разницу без правила двух часов (задача 13); переполнение остатка
                // (пополнили мимо API) ловит CHECK remaining_visits >= 0 — 500 и откат, как у отмены.
                pass.RemainingVisits -= seats - oldSeats;
                pass.Version++;
                enrollment.Seats = seats;
                await _unitOfWork.SaveChangesAsync(token);
                promotions = await PromoteAsync(item, queue, passes, userId, now, token);
            }

            // решение: карточка читается под замками до коммита: в ней ровно результат операции.
            var card = await _unitOfWork.Enrollment.GetCardAsync(id, userId, token)
                ?? throw new InvalidOperationException($"Enrollment {id} disappeared under lock.");

            return TransactionResult.Commit<ChangeSeatsResult>(new ChangeSeatsResult.Changed(
                card,
                oldSeats,
                promotions,
                seats == oldSeats ? new Dictionary<long, long>() : Versions(passes, userId, promotions)));
        }, ct);

        if (result is ChangeSeatsResult.Changed { Card: var changed, OldSeats: var old, Promotions: var promoted })
        {
            var remaining = changed.Pass.RemainingVisits;

            if (changed.Seats > old)
            {
                Log.EnrollmentSeatsIncreased(_logger, id, changed.Class.Id, old, changed.Seats, userId, changed.Seats - old, remaining);
                _metrics.VisitsCharged(changed.Seats - old);
            }
            else if (changed.Seats < old)
            {
                Log.EnrollmentSeatsDecreased(_logger, id, changed.Class.Id, old, changed.Seats, userId, old - changed.Seats, remaining);
                _metrics.VisitsRefunded(old - changed.Seats);
            }

            ObservePromotions(changed.Class.Id, promoted);
        }

        return result;
    }

    private void ObservePromotions(long classId, IReadOnlyList<PromotionOutcome> promotions)
    {
        foreach (var (entry, skipReason, remaining) in promotions)
        {
            if (skipReason is null)
            {
                Log.WaitlistPromoted(_logger, entry.Id, entry.Id, classId, entry.Seats, entry.UserId, remaining);
                _metrics.WaitlistPromoted(entry.Seats);
                _metrics.VisitsCharged(entry.Seats);
            }
            else
            {
                Log.WaitlistPromotionSkipped(_logger, entry.Id, classId, entry.Seats, entry.UserId, skipReason);
            }
        }
    }

    private async Task<IReadOnlyList<PromotionOutcome>> PromoteAsync(
        Class item,
        List<WaitlistEntry> queue,
        List<Pass> passes,
        long userId,
        DateTime now,
        CancellationToken ct)
    {
        if (queue.Count == 0)
        {
            return [];
        }

        var freeSeats = item.Capacity - await _unitOfWork.Enrollment.SumActiveSeatsAsync(item.Id, ct);
        var passesByUser = passes.ToDictionary(x => x.UserId);
        var steps = WaitlistPromotion.Plan(freeSeats, queue, passesByUser, userId, DateOnly.FromDateTime(now));

        foreach (var step in steps.Where(x => x.SkipReason is null))
        {
            var entry = step.Entry;
            entry.Status = WaitlistStatuses.Promoted;
            passesByUser[entry.UserId].RemainingVisits -= entry.Seats;

            // решение: бронь продвинутого — с id его записи в очереди (задача 18).
            await _unitOfWork.Enrollment.CreateAsync(
                new Enrollment
                {
                    Id = entry.Id,
                    UserId = entry.UserId,
                    ClassId = item.Id,
                    Seats = entry.Seats,
                    Status = EnrollmentStatuses.Active,
                    CreatedAt = now.TruncateToMicroseconds()
                },
                ct);
        }

        foreach (var promotedUserId in steps.Where(x => x.SkipReason is null).Select(x => x.Entry.UserId).Distinct())
        {
            passesByUser[promotedUserId].Version++;
        }

        await _unitOfWork.SaveChangesAsync(ct);

        // решение: пропуск из-за мест не наблюдается (задача 23): иначе каждая отмена логировала бы всю очередь.
        return steps
            .Where(x => x.SkipReason != WaitlistPromotion.NoSeats)
            .Select(x => new PromotionOutcome(x.Entry, x.SkipReason, passesByUser[x.Entry.UserId].RemainingVisits))
            .ToList();
    }

    private static IReadOnlyDictionary<long, long> Versions(List<Pass> passes, long userId, IReadOnlyList<PromotionOutcome> promotions)
    {
        var promoted = promotions.Where(x => x.SkipReason is null).Select(x => x.Entry.UserId).ToHashSet();

        return passes.Where(x => x.UserId == userId || promoted.Contains(x.UserId)).ToDictionary(x => x.UserId, x => x.Version);
    }

    private static CancelResult Repeat(Enrollment enrollment)
    {
        var refundedVisits = enrollment.RefundedVisits
            ?? throw new InvalidOperationException($"Enrollment {enrollment.Id} is cancelled without refunded visits.");

        return new CancelResult.Cancelled(enrollment.Id, enrollment.ClassId, enrollment.Seats, refundedVisits, null, true, [], new Dictionary<long, long>());
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
            pass.Version++;

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

            return TransactionResult.Commit<EnrollResult>(new EnrollResult.Created(
                enrollment,
                pass.RemainingVisits,
                new Dictionary<long, long> { [userId] = pass.Version }));
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
