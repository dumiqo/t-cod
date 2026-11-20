using DataAccessLayer.Repositories;
using WebApi.Waitlist;

namespace WebApi.Enrollments;

public abstract record ChangeSeatsResult
{
    public sealed record Changed(EnrollmentCard Card, int OldSeats, IReadOnlyList<PromotionOutcome> Promotions, IReadOnlyDictionary<long, long> Versions) : ChangeSeatsResult;

    public sealed record NotFound : ChangeSeatsResult;

    public sealed record Rejected(string Reason) : ChangeSeatsResult;
}
