using DataAccessLayer.Entities;

namespace WebApi.Waitlist;

public sealed record JoinWaitlistResponse(long Id, long ClassId, int Seats, int Position, DateTime CreatedAt)
{
    public static JoinWaitlistResponse From(WaitlistEntry entry, int position)
    {
        return new JoinWaitlistResponse(entry.Id, entry.ClassId, entry.Seats, position, entry.CreatedAt);
    }

    public static JoinWaitlistResponse Replay(IdempotencyKey key)
    {
        if (key.ResultId is not { } id || key.ResultCreatedAt is not { } createdAt || key.ResultPosition is not { } position)
        {
            throw new InvalidOperationException($"Idempotency key {key.Id} is committed without a result.");
        }

        return new JoinWaitlistResponse(id, key.ClassId, key.Seats, position, createdAt);
    }
}
