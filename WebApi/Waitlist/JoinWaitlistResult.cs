using DataAccessLayer.Entities;

namespace WebApi.Waitlist;

public abstract record JoinWaitlistResult
{
    public sealed record Created(WaitlistEntry Entry, int Position) : JoinWaitlistResult;

    public sealed record ClassNotFound : JoinWaitlistResult;
}
