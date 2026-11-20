namespace WebApi.Redis;

public sealed class RedisSettings
{
    public const string Section = "Redis";

    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(1);
}
