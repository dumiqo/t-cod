using DataAccessLayer.Entities;

namespace WebApi.Waitlist;

public sealed record JoinWaitlistResponse(long Id, long ClassId, int Seats, int Position, DateTime CreatedAt)
{
    public static JoinWaitlistResponse From(WaitlistEntry entry, int position)
    {
        return new JoinWaitlistResponse(entry.Id, entry.ClassId, entry.Seats, position, entry.CreatedAt);
    }
}
