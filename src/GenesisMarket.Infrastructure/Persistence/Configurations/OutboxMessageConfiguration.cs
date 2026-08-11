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
        b.Property(m => m.Payload).HasMaxLength(1024).IsRequired();
        b.Property(m => m.LastError).HasMaxLength(2048);

        // Обработчик выбирает необработанные (ProcessedAt IS NULL) по возрастанию CreatedAt.
        b.HasIndex(m => new { m.ProcessedAt, m.CreatedAt });
    }
}
