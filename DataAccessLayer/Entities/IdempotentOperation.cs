namespace DataAccessLayer.Entities;

public enum IdempotentOperation
{
    Enroll,
    JoinWaitlist
}

public static class IdempotentOperations
{
    public static string ToValue(IdempotentOperation operation)
    {
        return operation switch
        {
            IdempotentOperation.Enroll => "enroll",
            IdempotentOperation.JoinWaitlist => "join-waitlist",
            _ => throw new InvalidOperationException($"Unknown idempotent operation {operation}.")
        };
    }

    public static IdempotentOperation Parse(string value)
    {
        return value switch
        {
            "enroll" => IdempotentOperation.Enroll,
            "join-waitlist" => IdempotentOperation.JoinWaitlist,
            _ => throw new InvalidOperationException($"Unknown idempotent operation '{value}'.")
        };
    }
}
