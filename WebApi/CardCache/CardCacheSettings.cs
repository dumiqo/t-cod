namespace WebApi.CardCache;

public sealed class CardCacheSettings
{
    public const string Section = "Cache";

    public TimeSpan CardTtl { get; set; } = TimeSpan.FromMinutes(5);
}
