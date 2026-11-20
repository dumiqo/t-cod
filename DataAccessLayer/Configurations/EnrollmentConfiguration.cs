using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class EnrollmentConfiguration : IEntityTypeConfiguration<Enrollment>
{
    public void Configure(EntityTypeBuilder<Enrollment> builder)
    {
        builder.ToTable("enrollments", x =>
        {
            x.HasCheckConstraint("ck_enrollments_seats", "seats >= 1");
            x.HasCheckConstraint("ck_enrollments_refunded_visits_status", "(status = 'active') = (refunded_visits IS NULL)");
            x.HasCheckConstraint("ck_enrollments_refunded_visits", "refunded_visits >= 0");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.Status).HasColumnName("status").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.CancelledAt).HasColumnName("cancelled_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.RefundedVisits).HasColumnName("refunded_visits");

        builder
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId);

        builder
            .HasOne(x => x.Class)
            .WithMany(x => x.Enrollments)
            .HasForeignKey(x => x.ClassId);

        // решение: FK-индекс объявлен явно — иначе EF удаляет его в пользу частичного (class_id, user_id), а проверкам FK нужен полный.
        builder.HasIndex(x => x.ClassId);
        builder
            .HasIndex(x => new { x.ClassId, x.UserId })
            .IncludeProperties(x => x.Seats)
            .HasFilter("status = 'active'");
    }
}
