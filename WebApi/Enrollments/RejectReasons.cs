namespace WebApi.Enrollments;

public static class RejectReasons
{
    public const string IdempotencyKeyReused = "idempotency-key-reused";

    public const string EnrollmentCancelled = "enrollment-cancelled";

    public const string ClassCancelled = "class-cancelled";

    public const string ClassStarted = "class-started";

    public const string PassExpired = "pass-expired";

    public const string PassExhausted = "pass-exhausted";

    public const string CapacityExceeded = "capacity-exceeded";
}
