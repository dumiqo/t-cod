using Common.Database.Repositories.Generic;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataAccessLayer.Repositories;

public class IdempotencyKeyRepository : GenericRepository<IdempotencyKey>, IIdempotencyKeyRepository
{
    public IdempotencyKeyRepository(DbSet<IdempotencyKey> dbSet)
        : base(dbSet)
    {
    }

    public async Task<IdempotencyKeyClaim> ClaimAsync(IdempotencyKey candidate, CancellationToken ct = default)
    {
        var operation = IdempotentOperations.ToValue(candidate.Operation);

        // решение: ON CONFLICT DO NOTHING — запрос с тем же ключом ждёт на уникальном индексе, а не получает 23505:
        // транзакция не обрывается, и после коммита первого повтор находит его строку.
        var inserted = await _dbSet
            .FromSql($"""
                INSERT INTO idempotency_keys (user_id, operation, key_hash, class_id, seats)
                VALUES ({candidate.UserId}, {operation}, {candidate.KeyHash}, {candidate.ClassId}, {candidate.Seats})
                ON CONFLICT (user_id, operation, key_hash) DO NOTHING
                RETURNING *
                """)
            .ToListAsync(ct);

        if (inserted.Count == 1)
        {
            return new IdempotencyKeyClaim(true, inserted[0]);
        }

        var existing = await _dbSet.SingleAsync(
            x => x.UserId == candidate.UserId && x.Operation == candidate.Operation && x.KeyHash == candidate.KeyHash,
            ct);

        return new IdempotencyKeyClaim(false, existing);
    }
}
