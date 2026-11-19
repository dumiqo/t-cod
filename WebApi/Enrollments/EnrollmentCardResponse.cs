using DataAccessLayer.Repositories;

namespace WebApi.Enrollments;

public sealed record EnrollmentCardResponse(
    long Id,
    string Status,
    int Seats,
    DateTime CreatedAt,
    EnrollmentCardResponse.ClassPart Class,
    EnrollmentCardResponse.PassPart Pass)
{
    public static EnrollmentCardResponse From(EnrollmentCard card)
    {
        return new EnrollmentCardResponse(
            card.Id,
            card.Status,
            card.Seats,
            card.CreatedAt,
            new ClassPart(
                card.Class.Id,
                card.Class.Title,
                card.Class.StartsAt,
                card.Class.DurationMinutes,
                card.Class.IsCancelled,
                new RoomPart(card.Class.Room.Id, card.Class.Room.Name)),
            new PassPart(card.Pass.RemainingVisits, card.Pass.ValidUntil));
    }

    public sealed record ClassPart(long Id, string Title, DateTime StartsAt, int DurationMinutes, bool IsCancelled, RoomPart Room);

    public sealed record RoomPart(long Id, string Name);

    public sealed record PassPart(int RemainingVisits, DateOnly ValidUntil);
}
