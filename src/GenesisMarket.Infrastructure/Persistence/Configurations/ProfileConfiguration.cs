using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class ProfileConfiguration : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> b)
    {
        b.ToTable("profiles", t =>
            t.HasCheckConstraint(
                "ck_profiles_display_name_length",
                "char_length(\"DisplayName\") <= 60"));

        // PK = FK на users.Id (общий ключ 1:1).
        b.HasKey(p => p.UserId);

        b.Property(p => p.DisplayName).HasMaxLength(60).IsRequired();

        // City — native enum-тип.
        b.Property(p => p.City).IsRequired();

        b.Property(p => p.AvatarUrl).HasMaxLength(512);
        b.Property(p => p.TelegramUsername).HasMaxLength(64);

        b.Property(p => p.ShowPhoneInListing).HasDefaultValue(true);
        b.Property(p => p.ViberEnabled).HasDefaultValue(false);
        b.Property(p => p.WhatsappEnabled).HasDefaultValue(false);
    }
}
