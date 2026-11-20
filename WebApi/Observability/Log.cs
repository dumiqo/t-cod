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
        EventId = 1005,
        EventName = "RedisFailure",
        Level = LogLevel.Warning,
        Message = "Redis failure: {Operation}, {Reason}")]
    public static partial void RedisFailure(ILogger logger, Exception? exception, string operation, string reason);

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

    [LoggerMessage(
        EventId = 3001,
        EventName = "WaitlistJoined",
        Level = LogLevel.Information,
        Message = "Waitlist joined: {WaitlistId} class {ClassId} seats {Seats} for user {UserId}, position {Position}")]
    public static partial void WaitlistJoined(ILogger logger, long waitlistId, long classId, int seats, long userId, int position);

    [LoggerMessage(
        EventId = 3002,
        EventName = "WaitlistLeft",
        Level = LogLevel.Information,
        Message = "Waitlist left: {WaitlistId} class {ClassId} seats {Seats} for user {UserId}")]
    public static partial void WaitlistLeft(ILogger logger, long waitlistId, long classId, int seats, long userId);

    [LoggerMessage(
        EventId = 3003,
        EventName = "WaitlistPromoted",
        Level = LogLevel.Information,
        Message = "Waitlist promoted: {WaitlistId} to enrollment {EnrollmentId} class {ClassId} seats {Seats} for user {UserId}, remaining {RemainingVisits}")]
    public static partial void WaitlistPromoted(
        ILogger logger,
        long waitlistId,
        long enrollmentId,
        long classId,
        int seats,
        long userId,
        int remainingVisits);

    [LoggerMessage(
        EventId = 3004,
        EventName = "WaitlistPromotionSkipped",
        Level = LogLevel.Information,
        Message = "Waitlist promotion skipped: {WaitlistId} class {ClassId} seats {Seats} for user {UserId}, reason {Reason}")]
    public static partial void WaitlistPromotionSkipped(ILogger logger, long waitlistId, long classId, int seats, long userId, string reason);
}
