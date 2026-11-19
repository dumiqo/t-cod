using DataAccessLayer.Entities;

namespace WebApi.Enrollments;

public abstract record EnrollResult
{
    public sealed record Created(Enrollment Enrollment, int RemainingVisits) : EnrollResult;

    public sealed record ClassNotFound : EnrollResult;

    public sealed record Rejected(string Reason) : EnrollResult;
}
