using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class ContactRevealConfiguration : IEntityTypeConfiguration<ContactReveal>
{
    public void Configure(EntityTypeBuilder<ContactReveal> b)
    {
        b.ToTable("contact_reveals");
        b.HasKey(r => r.Id);

        // Hex HMAC-SHA256 — ровно 64 символа. Сырой IP тут не хранится.
        b.Property(r => r.IpHash).HasMaxLength(64).IsRequired();

        // Агрегат contactRevealCount по объявлению.
        b.HasIndex(r => r.ListingId);

        // Порог алерта: сколько раскрытий с одного IpHash за час.
        b.HasIndex(r => new { r.IpHash, r.CreatedAt });
    }
}
