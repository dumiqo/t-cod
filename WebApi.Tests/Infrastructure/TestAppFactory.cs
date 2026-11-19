using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace WebApi.Tests.Infrastructure;

internal sealed class TestAppFactory : ApiApplicationFactory
{
    private readonly string _connectionString;
    private readonly FakeTimeProvider _clock;
    private readonly string _redisKeyPrefix;

    public TestAppFactory(string connectionString, FakeTimeProvider clock, string redisKeyPrefix)
    {
        _connectionString = connectionString;
        _clock = clock;
        _redisKeyPrefix = redisKeyPrefix;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _connectionString
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);

            services.RemoveAll<IConnectionMultiplexer>();
            // решение: своё подключение к Redis на хост, как в продакшене: оно закрывается вместе с хостом,
            // а префикс живёт в обёртке, и общий мультиплексор между тестами не нужен.
            services.AddSingleton(_ => KeyPrefixedConnection.Wrap(
                ConnectionMultiplexer.Connect(TestDatabase.RedisConnectionString),
                _redisKeyPrefix));

            services.AddControllers().AddApplicationPart(typeof(TestAppFactory).Assembly);
        });
    }
}
