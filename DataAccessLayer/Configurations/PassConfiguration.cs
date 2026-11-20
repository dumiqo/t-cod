using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class PassConfiguration : IEntityTypeConfiguration<Pass>
{
    public void Configure(EntityTypeBuilder<Pass> builder)
    {
        builder.ToTable("passes", x => x.HasCheckConstraint("ck_passes_remaining_visits", "remaining_visits >= 0"));

        builder.HasIndex(x => x.UserId).IsUnique();

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.RemainingVisits).HasColumnName("remaining_visits");
        builder.Property(x => x.ValidUntil).HasColumnName("valid_until").HasColumnType("date");
        builder.Property(x => x.Version).HasColumnName("version").HasDefaultValue(0L);

        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId);
    }
}
