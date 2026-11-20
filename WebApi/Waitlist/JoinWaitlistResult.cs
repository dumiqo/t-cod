using DataAccessLayer.Entities;

namespace WebApi.Waitlist;

public abstract record JoinWaitlistResult
{
    public sealed record Created(WaitlistEntry Entry, int Position) : JoinWaitlistResult;

    public sealed record Replayed(JoinWaitlistResponse Response) : JoinWaitlistResult;

    public sealed record ClassNotFound : JoinWaitlistResult;

    public sealed record Rejected(string Reason) : JoinWaitlistResult;
}
