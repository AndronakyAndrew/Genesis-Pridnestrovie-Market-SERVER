using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class BlockedCardConfiguration : IEntityTypeConfiguration<BlockedCard>
{
    public void Configure(EntityTypeBuilder<BlockedCard> b)
    {
        b.ToTable("blocked_cards", t =>
        {
            t.HasCheckConstraint("ck_blocked_cards_last4", "\"Last4\" ~ '^[0-9]{4}$'");
            t.HasCheckConstraint("ck_blocked_cards_reason_length", "char_length(\"Reason\") <= 500");
        });

        b.HasKey(c => c.Id);

        b.Property(c => c.CardHash).HasMaxLength(64).IsRequired();
        b.Property(c => c.Last4).HasMaxLength(4).IsRequired();
        b.Property(c => c.Reason).HasMaxLength(500).IsRequired();

        // Сверка текста объявления — по хешу; одна карта — одна запись.
        b.HasIndex(c => c.CardHash).IsUnique();
        b.HasIndex(c => c.CreatedAt);
    }
}
