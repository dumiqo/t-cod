using DataAccessLayer.Repositories;

namespace WebApi.Enrollments;

public abstract record ChangeSeatsResult
{
    public sealed record Changed(EnrollmentCard Card, int OldSeats) : ChangeSeatsResult;

    public sealed record NotFound : ChangeSeatsResult;

    public sealed record Rejected(string Reason) : ChangeSeatsResult;
}
