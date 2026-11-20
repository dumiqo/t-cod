namespace WebApi.Observability;

public static partial class Log
{
    [LoggerMessage(
        EventId = 1001,
        EventName = "RequestRejected",
        Level = LogLevel.Information,
        Message = "Request rejected: {Operation} {Reason} for user {UserId}")]
    public static partial void RequestRejected(ILogger logger, string operation, string reason, long userId);

    [LoggerMessage(
        EventId = 1002,
        EventName = "TransactionConflict",
        Level = LogLevel.Warning,
        Message = "Transaction conflict: {Operation} {Reason} for user {UserId}")]
    public static partial void TransactionConflict(
        ILogger logger,
        Exception exception,
        string operation,
        string reason,
        long userId);

    [LoggerMessage(
        EventId = 1004,
        EventName = "IdempotentReplay",
        Level = LogLevel.Information,
        Message = "Idempotent replay: {Operation} result {ResultId} class {ClassId} for user {UserId}")]
    public static partial void IdempotentReplay(ILogger logger, string operation, long resultId, long classId, long userId);

    [LoggerMessage(
        EventId = 2001,
        EventName = "EnrollmentCreated",
        Level = LogLevel.Information,
        Message = "Enrollment created: {EnrollmentId} class {ClassId} seats {Seats} for user {UserId}, remaining {RemainingVisits}")]
    public static partial void EnrollmentCreated(
        ILogger logger,
        long enrollmentId,
        long classId,
        int seats,
        long userId,
        int remainingVisits);

    [LoggerMessage(
        EventId = 2002,
        EventName = "EnrollmentCancelled",
        Level = LogLevel.Information,
        Message = "Enrollment cancelled: {EnrollmentId} class {ClassId} seats {Seats} for user {UserId}, refunded {RefundedVisits}, remaining {RemainingVisits}")]
    public static partial void EnrollmentCancelled(
        ILogger logger,
        long enrollmentId,
        long classId,
        int seats,
        long userId,
        int refundedVisits,
        int remainingVisits);

    [LoggerMessage(
        EventId = 2003,
        EventName = "EnrollmentCancelledWithoutRefund",
        Level = LogLevel.Information,
        Message = "Enrollment cancelled without refund: {EnrollmentId} class {ClassId} seats {Seats} for user {UserId}, remaining {RemainingVisits}")]
    public static partial void EnrollmentCancelledWithoutRefund(
        ILogger logger,
        long enrollmentId,
        long classId,
        int seats,
        long userId,
        int remainingVisits);

    [LoggerMessage(
        EventId = 2004,
        EventName = "EnrollmentSeatsIncreased",
        Level = LogLevel.Information,
        Message = "Enrollment seats increased: {EnrollmentId} class {ClassId} seats {OldSeats} -> {NewSeats} for user {UserId}, charged {ChargedVisits}, remaining {RemainingVisits}")]
    public static partial void EnrollmentSeatsIncreased(
        ILogger logger,
        long enrollmentId,
        long classId,
        int oldSeats,
        int newSeats,
        long userId,
        int chargedVisits,
        int remainingVisits);

    [LoggerMessage(
        EventId = 2005,
        EventName = "EnrollmentSeatsDecreased",
        Level = LogLevel.Information,
        Message = "Enrollment seats decreased: {EnrollmentId} class {ClassId} seats {OldSeats} -> {NewSeats} for user {UserId}, refunded {RefundedVisits}, remaining {RemainingVisits}")]
    public static partial void EnrollmentSeatsDecreased(
        ILogger logger,
        long enrollmentId,
        long classId,
        int oldSeats,
        int newSeats,
        long userId,
        int refundedVisits,
        int remainingVisits);
}
