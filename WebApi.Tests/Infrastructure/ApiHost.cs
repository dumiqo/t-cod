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
                return _factory ??= new ApiApplicationFactory();
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
