namespace DataAccessLayer.Repositories;

public sealed record EnrollmentCard(
    long Id,
    string Status,
    int Seats,
    DateTime CreatedAt,
    EnrollmentCardClass Class,
    EnrollmentCardPass Pass);

public sealed record EnrollmentCardClass(
    long Id,
    string Title,
    DateTime StartsAt,
    int DurationMinutes,
    bool IsCancelled,
    EnrollmentCardRoom Room);

public sealed record EnrollmentCardRoom(long Id, string Name);

public sealed record EnrollmentCardPass(int RemainingVisits, DateOnly ValidUntil);
