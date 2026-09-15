using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class LinkClickConfiguration : IEntityTypeConfiguration<LinkClick>
{
    public void Configure(EntityTypeBuilder<LinkClick> b)
    {
        b.ToTable("link_clicks");
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).UseIdentityByDefaultColumn();

        b.Property(c => c.Source).HasMaxLength(32).IsRequired();

        // Hex HMAC-SHA256 — ровно 64 символа, как в contact_reveals. Сырой IP тут не хранится.
        b.Property(c => c.IpHash).HasMaxLength(64).IsRequired();

        // FK на listings намеренно нет (как у contact_reveals): журнал append-only,
        // вставка на горячем пути перехода не должна платить за проверку ссылки.

        // Переходы по объявлению за период.
        b.HasIndex(c => new { c.ListingId, c.CreatedAt });
    }
}
