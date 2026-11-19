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
}
