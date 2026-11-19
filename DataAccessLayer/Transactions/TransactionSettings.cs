namespace DataAccessLayer.Transactions;

public sealed class TransactionSettings
{
    public const string Section = "Transactions";

    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
