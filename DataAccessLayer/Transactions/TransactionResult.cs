namespace DataAccessLayer.Transactions;

public readonly record struct TransactionResult<T>(T Value, bool Commit);

public static class TransactionResult
{
    public static TransactionResult<T> Commit<T>(T value)
    {
        return new TransactionResult<T>(value, true);
    }

    public static TransactionResult<T> Rollback<T>(T value)
    {
        return new TransactionResult<T>(value, false);
    }
}
