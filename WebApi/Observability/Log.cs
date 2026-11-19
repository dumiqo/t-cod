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
}
