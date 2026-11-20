namespace WebApi.Enrollments;

public abstract record CancelResult
{
    public sealed record Cancelled(long Id, long ClassId, int Seats, int RefundedVisits, int? RemainingVisits, bool Repeated) : CancelResult;

    public sealed record NotFound : CancelResult;

    public sealed record Rejected(string Reason) : CancelResult;
}
