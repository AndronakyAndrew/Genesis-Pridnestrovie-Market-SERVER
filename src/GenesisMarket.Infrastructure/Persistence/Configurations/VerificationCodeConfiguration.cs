using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class VerificationCodeConfiguration : IEntityTypeConfiguration<VerificationCode>
{
    public void Configure(EntityTypeBuilder<VerificationCode> b)
    {
        b.ToTable("verification_codes");
        b.HasKey(c => c.Id);

        // Канал хранится строкой (не входит в набор native enum-ов).
        b.Property(c => c.Channel).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(c => c.Target).HasMaxLength(256).IsRequired();
        b.Property(c => c.CodeHash).IsRequired();

        // Быстрый доступ к последнему коду пользователя по каналу.
        b.HasIndex(c => new { c.UserId, c.Channel, c.CreatedAt });

        b.HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
