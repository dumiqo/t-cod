using Common.Database.Interfaces;

namespace DataAccessLayer.Entities;

public class IdempotencyKey : IEntity
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public IdempotentOperation Operation { get; set; }

    public byte[] KeyHash { get; set; } = null!;

    public long ClassId { get; set; }

    public int Seats { get; set; }

    public long? ResultId { get; set; }

    public DateTime? ResultCreatedAt { get; set; }

    public int? ResultPosition { get; set; }
}
