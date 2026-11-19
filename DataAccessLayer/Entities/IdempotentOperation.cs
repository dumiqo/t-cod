namespace DataAccessLayer.Entities;

public enum IdempotentOperation
{
    Enroll
}

public static class IdempotentOperations
{
    public static string ToValue(IdempotentOperation operation)
    {
        return operation switch
        {
            IdempotentOperation.Enroll => "enroll",
            _ => throw new InvalidOperationException($"Unknown idempotent operation {operation}.")
        };
    }

    public static IdempotentOperation Parse(string value)
    {
        return value switch
        {
            "enroll" => IdempotentOperation.Enroll,
            _ => throw new InvalidOperationException($"Unknown idempotent operation '{value}'.")
        };
    }
}
