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

        // Поиск по хешу токена при refresh/logout — по нему же уникальность.
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.UserId);

        b.HasOne(t => t.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
