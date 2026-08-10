using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class PhoneVerificationCodeConfiguration : IEntityTypeConfiguration<PhoneVerificationCode>
{
    public void Configure(EntityTypeBuilder<PhoneVerificationCode> b)
    {
        b.ToTable("phone_verification_codes");
        b.HasKey(c => c.Id);

        b.Property(c => c.Phone).HasMaxLength(20).IsRequired();
        b.Property(c => c.CodeHash).IsRequired();

        // Быстрый доступ к последнему коду пользователя.
        b.HasIndex(c => new { c.UserId, c.CreatedAt });

        b.HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
