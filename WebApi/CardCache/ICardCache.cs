using DataAccessLayer.Repositories;

namespace WebApi.CardCache;

public interface ICardCache
{
    Task<(bool Available, EnrollmentCard? Card)> GetAsync(long userId, long id, CancellationToken ct);

    Task FillAsync(long userId, VersionedCard card, CancellationToken ct);

    Task InvalidateAsync(long userId, long version);
}
