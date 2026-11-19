namespace DataAccessLayer.Transactions;

public sealed class TransactionConflictException : Exception
{
    public const string LockTimeout = "lock-timeout";

    public const string Deadlock = "deadlock";

    public TransactionConflictException(string operation, string reason, Exception innerException)
        : base($"Transaction of {operation} aborted: {reason}.", innerException)
    {
        Operation = operation;
        Reason = reason;
    }

    public string Operation { get; }

    public string Reason { get; }
}
