using DataAccessLayer.Entities;

namespace WebApi.Enrollments;

public abstract record EnrollResult
{
    public sealed record Created(Enrollment Enrollment, int RemainingVisits, IReadOnlyDictionary<long, long> Versions) : EnrollResult;

    public sealed record Replayed(EnrollmentResponse Response) : EnrollResult;

    public sealed record ClassNotFound : EnrollResult;

    public sealed record Rejected(string Reason) : EnrollResult;
}
