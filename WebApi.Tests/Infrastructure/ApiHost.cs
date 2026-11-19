namespace WebApi.Tests.Infrastructure;

public static class ApiHost
{
    private static readonly object Sync = new();

    private static ApiApplicationFactory? _factory;

    public static ApiApplicationFactory Factory
    {
        get
        {
            lock (Sync)
            {
                if (_factory == null)
                {
                    var factory = new ApiApplicationFactory();

                    // решение: сервер стартует под замком — WebApplicationFactory 9.0 поднимает его без синхронизации,
                    // и параллельные тесты подняли бы два хоста, засевающих одну базу.
                    _ = factory.Server;
                    _factory = factory;
                }

                return _factory;
            }
        }
    }

    public static HttpClient CreateClient()
    {
        return Factory.CreateClient();
    }

    public static async Task DisposeAsync()
    {
        ApiApplicationFactory? factory;

        lock (Sync)
        {
            factory = _factory;
            _factory = null;
        }

        if (factory != null)
        {
            await factory.DisposeAsync();
        }
    }
}
