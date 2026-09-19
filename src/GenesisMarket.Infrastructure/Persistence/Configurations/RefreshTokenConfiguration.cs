using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_tokens");
        b.HasKey(t => t.Id);

        b.Property(t => t.TokenHash).IsRequired();
        b.Property(t => t.CreatedByIpHash).HasMaxLength(128);

        // ---- Сессия ----
        // Семейства из User-Agent. Сырой User-Agent не хранится, колонки под него нет.
        b.Property(t => t.DeviceFamily).HasMaxLength(32);
        b.Property(t => t.BrowserFamily).HasMaxLength(32);
        b.Property(t => t.OsFamily).HasMaxLength(32);

        // Два октета IPv4 либо два хекстета IPv6 — единственная открытая часть адреса.
        b.Property(t => t.IpPrefix).HasMaxLength(39);

        // Поиск по хешу токена при refresh/logout — по нему же уникальность.
        b.HasIndex(t => t.TokenHash).IsUnique();

        // Сетевой сигнал для модератора: другие аккаунты, входившие с того же адреса.
        // Сравнение по HMAC — сырой IP не хранится и для сигнала не нужен.
        b.HasIndex(t => t.CreatedByIpHash);
        b.HasIndex(t => t.UserId);

        // Список сессий и отзыв по SessionId: выборка всегда в пределах владельца.
        b.HasIndex(t => new { t.UserId, t.SessionId });

        b.HasOne(t => t.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
