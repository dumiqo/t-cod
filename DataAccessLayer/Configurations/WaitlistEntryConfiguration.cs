using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.ToTable("waitlist_entries", x =>
        {
            x.HasCheckConstraint("ck_waitlist_entries_seats", "seats >= 1");
            x.HasCheckConstraint("ck_waitlist_entries_status", "status IN ('waiting', 'cancelled', 'promoted')");
        });

        builder.HasKey(x => x.Id);

        // решение: id из последовательности enrollments (задача 18) — продвинутая запись станет бронью с тем же id.
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("nextval('enrollments_id_seq'::regclass)");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.CancelledAt).HasColumnName("cancelled_at").HasColumnType("timestamp with time zone");

        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder
            .HasOne(x => x.Class)
            .WithMany()
            .HasForeignKey(x => x.ClassId);

        builder.HasIndex(x => x.ClassId);
        builder.HasIndex(x => new { x.ClassId, x.Id }).HasFilter("status = 'waiting'");
        builder.HasIndex(x => new { x.ClassId, x.UserId }).IsUnique().HasFilter("status = 'waiting'");
    }
}
