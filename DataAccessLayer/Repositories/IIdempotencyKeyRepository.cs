using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;

namespace DataAccessLayer.Repositories;

public sealed record IdempotencyKeyClaim(bool IsNew, IdempotencyKey Key);

public interface IIdempotencyKeyRepository : IGenericRepository<IdempotencyKey>
{
    Task<IdempotencyKeyClaim> ClaimAsync(IdempotencyKey candidate, CancellationToken ct = default);
}
