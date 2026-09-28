using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class UserTwoFactorConfiguration : IEntityTypeConfiguration<UserTwoFactor>
{
    public void Configure(EntityTypeBuilder<UserTwoFactor> b)
    {
        b.ToTable("user_two_factor");
        b.HasKey(t => t.UserId);

        b.Property(t => t.SecretCipher).IsRequired();
        b.Property(t => t.LastUsedTimeStep).HasDefaultValue(0L);
        b.Ignore(t => t.IsEnabled);

        b.HasOne(t => t.User)
            .WithOne(u => u.TwoFactor)
            .HasForeignKey<UserTwoFactor>(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(t => t.RecoveryCodes)
            .WithOne()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> b)
    {
        b.ToTable("user_recovery_codes");
        b.HasKey(c => c.Id);
        b.Property(c => c.CodeHash).IsRequired();
        b.HasIndex(c => new { c.UserId, c.CodeHash }).IsUnique();
    }
}

public class MfaChallengeConfiguration : IEntityTypeConfiguration<MfaChallenge>
{
    public void Configure(EntityTypeBuilder<MfaChallenge> b)
    {
        b.ToTable("mfa_challenges");
        b.HasKey(c => c.Id);
        b.Property(c => c.TokenHash).IsRequired();
        b.HasIndex(c => c.TokenHash).IsUnique();
        b.HasIndex(c => c.UserId);

        b.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
