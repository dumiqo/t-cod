using DataAccessLayer.Entities;

namespace WebApi.Enrollments;

public sealed record EnrollmentResponse(long Id, long ClassId, int Seats, string Status, DateTime CreatedAt)
{
    public static EnrollmentResponse From(Enrollment enrollment)
    {
        return new EnrollmentResponse(
            enrollment.Id,
            enrollment.ClassId,
            enrollment.Seats,
            enrollment.Status,
            enrollment.CreatedAt);
    }

    public static EnrollmentResponse Replay(IdempotencyKey key)
    {
        if (key.ResultId is not { } id || key.ResultCreatedAt is not { } createdAt)
        {
            throw new InvalidOperationException($"Idempotency key {key.Id} is committed without a result.");
        }

        return new EnrollmentResponse(id, key.ClassId, key.Seats, EnrollmentStatuses.Active, createdAt);
    }
}
