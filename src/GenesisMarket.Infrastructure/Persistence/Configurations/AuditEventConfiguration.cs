using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events", t =>
        {
            t.HasCheckConstraint("ck_audit_events_actor_kind",
                "\"ActorKind\" IN ('user', 'cli', 'system')");
            // Действие пользователя без исполнителя — ошибка записи, а не «неизвестный».
            t.HasCheckConstraint("ck_audit_events_user_actor",
                "\"ActorKind\" <> 'user' OR \"ActorId\" IS NOT NULL");
        });

        b.HasKey(e => e.Id);

        b.Property(e => e.ProjectKey).HasMaxLength(40).IsRequired();
        b.Property(e => e.ActorKind).HasMaxLength(10).IsRequired();
        b.Property(e => e.ActorLabel).HasMaxLength(200);
        b.Property(e => e.Action).HasMaxLength(60).IsRequired();
        b.Property(e => e.TargetType).HasMaxLength(30).IsRequired();
        b.Property(e => e.TargetId).HasMaxLength(64).IsRequired();
        b.Property(e => e.OldValue).HasColumnType("jsonb");
        b.Property(e => e.NewValue).HasColumnType("jsonb");
        b.Property(e => e.Reason).HasMaxLength(500);
        b.Property(e => e.RequestId).HasMaxLength(64);
        b.Property(e => e.IpHash).HasMaxLength(128);

        // Лента журнала (новые сверху) и её курсор.
        b.HasIndex(e => new { e.OccurredAt, e.Id });
        // «Что делал этот сотрудник».
        b.HasIndex(e => new { e.ActorId, e.OccurredAt });
        // История объекта.
        b.HasIndex(e => new { e.TargetType, e.TargetId });
        b.HasIndex(e => e.Action);
    }
}

public class RoleChangeConfiguration : IEntityTypeConfiguration<RoleChange>
{
    public void Configure(EntityTypeBuilder<RoleChange> b)
    {
        b.ToTable("role_changes", t =>
        {
            t.HasCheckConstraint("ck_role_changes_source", "\"Source\" IN ('ui', 'cli')");
        });

        b.HasKey(c => c.Id);

        b.Property(c => c.ProjectKey).HasMaxLength(40).IsRequired();
        b.Property(c => c.OldRole).HasMaxLength(20).IsRequired();
        b.Property(c => c.NewRole).HasMaxLength(20).IsRequired();
        b.Property(c => c.Source).HasMaxLength(10).IsRequired();
        b.Property(c => c.ActorLabel).HasMaxLength(200);
        b.Property(c => c.Reason).HasMaxLength(500);

        // FK на users нет намеренно: история ролей переживает удаление аккаунта,
        // как и moderation_logs.
        b.HasIndex(c => new { c.UserId, c.OccurredAt });
        b.HasIndex(c => c.OccurredAt);
    }
}
