namespace WebApi.RateLimiting;

public static class RateLimitWindow
{
    // решение: 60 запросов и минута — контракт, а не параметр эксплуатации (задача 27).
    public const int Limit = 60;
    public const long Milliseconds = 60_000;
}
