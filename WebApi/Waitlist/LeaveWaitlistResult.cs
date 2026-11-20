namespace WebApi.Waitlist;

public abstract record LeaveWaitlistResult
{
    public sealed record Left(long Id) : LeaveWaitlistResult;

    public sealed record NotFound : LeaveWaitlistResult;

    public sealed record Rejected(string Reason) : LeaveWaitlistResult;
}
