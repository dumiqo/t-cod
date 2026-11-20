using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys", x =>
        {
            x.HasCheckConstraint("ck_idempotency_keys_operation", "operation IN ('enroll', 'join-waitlist')");
            x.HasCheckConstraint("ck_idempotency_keys_key_hash", "octet_length(key_hash) = 32");
        });

        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.UserId, x.Operation, x.KeyHash }).IsUnique();

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Operation)
            .HasColumnName("operation")
            .HasColumnType("text")
            .HasConversion(x => IdempotentOperations.ToValue(x), x => IdempotentOperations.Parse(x));
        builder.Property(x => x.KeyHash).HasColumnName("key_hash").IsRequired();
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.ResultId).HasColumnName("result_id");
        builder.Property(x => x.ResultCreatedAt).HasColumnName("result_created_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ResultPosition).HasColumnName("result_position");

        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId);
    }
}
