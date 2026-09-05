using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> b)
    {
        b.ToTable("password_reset_tokens");
        b.HasKey(t => t.Id);

        b.Property(t => t.TokenHash).IsRequired();
        b.Property(t => t.RequestedByIpHash).HasMaxLength(128);

        // По хешу ищем токен из ссылки; он же — гарантия отсутствия дублей.
        b.HasIndex(t => t.TokenHash).IsUnique();

        // Гашение прошлых запросов пользователя и кулдаун на повторную отправку.
        b.HasIndex(t => new { t.UserId, t.CreatedAt });

        b.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
