using DataAccessLayer.Repositories;

namespace WebApi.CardCache;

public interface ICardCache
{
    Task<EnrollmentCard?> GetAsync(long userId, long id);

    Task FillAsync(long userId, VersionedCard card);

    Task InvalidateAsync(long userId, long version);
}
