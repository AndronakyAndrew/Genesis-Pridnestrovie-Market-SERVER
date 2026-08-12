using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class ModerationLogConfiguration : IEntityTypeConfiguration<ModerationLog>
{
    public void Configure(EntityTypeBuilder<ModerationLog> b)
    {
        b.ToTable("moderation_logs", t =>
        {
            t.HasCheckConstraint("ck_moderation_logs_reason_length",
                "\"Reason\" IS NULL OR char_length(\"Reason\") <= 500");
        });

        b.HasKey(l => l.Id);

        b.Property(l => l.Action).HasMaxLength(40).IsRequired();
        b.Property(l => l.TargetType).HasMaxLength(20).IsRequired();
        b.Property(l => l.Reason).HasMaxLength(500);
        // PayloadJson — снимок решения, храним как jsonb.
        b.Property(l => l.PayloadJson).HasColumnType("jsonb");

        // Аудит-лента модератора: недавние действия конкретного модератора.
        b.HasIndex(l => new { l.ActorId, l.CreatedAt });

        // История по объекту (все действия над объявлением/пользователем/жалобой).
        b.HasIndex(l => new { l.TargetType, l.TargetId });

        // Счётчики за период (stats: сегодня/неделя).
        b.HasIndex(l => l.CreatedAt);
    }
}
