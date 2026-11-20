using StackExchange.Redis;

namespace WebApi.Redis;

public static class RedisConnection
{
    public static IConnectionMultiplexer Connect(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);

        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;

        return ConnectionMultiplexer.Connect(options);
    }
}
