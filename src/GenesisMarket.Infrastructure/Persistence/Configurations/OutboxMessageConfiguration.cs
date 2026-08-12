using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages");
        b.HasKey(m => m.Id);

        b.Property(m => m.Type).HasMaxLength(64).IsRequired();
        b.Property(m => m.Payload).HasMaxLength(4096).IsRequired();
        b.Property(m => m.Error).HasMaxLength(2048);

        // Статус — строкой (операционный enum, не входит в native enum-ы каталога).
        // Дефолт на уровне БД — Pending: новые строки и существующие при миграции валидны.
        b.Property(m => m.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(Domain.Enums.OutboxStatus.Pending)
            .IsRequired();

        // Дефолт NextAttemptAt — now(): существующая строка сразу готова к попытке;
        // при обычной вставке значение задаёт приложение.
        b.Property(m => m.NextAttemptAt).HasDefaultValueSql("now()");

        // Горячий путь диспетчера: выбрать Pending, у которых подошло время попытки,
        // по возрастанию CreatedAt (FIFO). Частичный индекс — только по обрабатываемым.
        b.HasIndex(m => new { m.Status, m.NextAttemptAt, m.CreatedAt })
            .HasDatabaseName("ix_outbox_due")
            .HasFilter("\"Status\" = 'Pending'");

        // Отдельный джоб-уборщик выбирает Done по ProcessedAt.
        b.HasIndex(m => m.ProcessedAt);
    }
}
