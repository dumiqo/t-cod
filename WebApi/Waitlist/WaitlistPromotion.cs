using DataAccessLayer.Entities;
using WebApi.Enrollments;

namespace WebApi.Waitlist;

public sealed record PromotionStep(WaitlistEntry Entry, string? SkipReason);

public sealed record PromotionOutcome(WaitlistEntry Entry, string? SkipReason, int RemainingVisits);

public static class WaitlistPromotion
{
    public const string OwnRelease = "own-release";

    public const string NoSeats = "no-seats";

    public static IReadOnlyList<PromotionStep> Plan(
        long freeSeats,
        IEnumerable<WaitlistEntry> entries,
        IReadOnlyDictionary<long, Pass> passesByUser,
        long ownUserId,
        DateOnly today)
    {
        var steps = new List<PromotionStep>();

        // решение: очередь проходится целиком по id, неподходящие пропускаются и остаются waiting (задачи 21, 22).
        foreach (var entry in entries.OrderBy(x => x.Id))
        {
            var reason = Check(entry, freeSeats, passesByUser, ownUserId, today);

            if (reason is null)
            {
                freeSeats -= entry.Seats;
            }

            steps.Add(new PromotionStep(entry, reason));
        }

        return steps;
    }

    private static string? Check(WaitlistEntry entry, long freeSeats, IReadOnlyDictionary<long, Pass> passesByUser, long ownUserId, DateOnly today)
    {
        if (entry.UserId == ownUserId)
        {
            return OwnRelease;
        }

        if (entry.Seats > freeSeats)
        {
            return NoSeats;
        }

        var pass = passesByUser.GetValueOrDefault(entry.UserId)
            ?? throw new InvalidOperationException($"User {entry.UserId} of waitlist entry {entry.Id} has no pass.");

        if (pass.ValidUntil < today)
        {
            return RejectReasons.PassExpired;
        }

        return pass.RemainingVisits < entry.Seats ? RejectReasons.PassExhausted : null;
    }
}
