using DataAccessLayer.Entities;

namespace WebApi.Enrollments;

public sealed record CancelEnrollmentResponse(long Id, string Status, int RefundedVisits)
{
    public static CancelEnrollmentResponse From(CancelResult.Cancelled cancelled)
    {
        return new CancelEnrollmentResponse(cancelled.Id, EnrollmentStatuses.Cancelled, cancelled.RefundedVisits);
    }
}
