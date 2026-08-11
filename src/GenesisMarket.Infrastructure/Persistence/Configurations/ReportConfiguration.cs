using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class ReportConfiguration : IEntityTypeConfiguration<Report>
{
    public void Configure(EntityTypeBuilder<Report> b)
    {
        b.ToTable("reports", t =>
        {
            t.HasCheckConstraint("ck_reports_comment_length",
                "\"Comment\" IS NULL OR char_length(\"Comment\") <= 500");
        });

        b.HasKey(r => r.Id);

        // Enum-ы жалоб — строками (не входят в native enum-ы каталога).
        b.Property(r => r.TargetType).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(r => r.Reason).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(r => r.Status)
            .HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(ReportStatus.New).IsRequired();

        b.Property(r => r.Comment).HasMaxLength(500);
        b.Property(r => r.ReporterIpHash).HasMaxLength(64);
        b.Property(r => r.Resolution).HasMaxLength(1000);

        // Очередь модерации: по статусу и свежести.
        b.HasIndex(r => new { r.Status, r.CreatedAt });

        // Дедуп и подсчёт независимых жалоб по объекту.
        b.HasIndex(r => new { r.TargetType, r.TargetId });

        // Дедуп жалобы авторизованного репортёра на конкретный объект.
        b.HasIndex(r => new { r.ReporterId, r.TargetType, r.TargetId });
    }
}
