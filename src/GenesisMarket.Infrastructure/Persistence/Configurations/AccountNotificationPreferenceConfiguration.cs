using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public sealed class AccountNotificationPreferenceConfiguration
    : IEntityTypeConfiguration<AccountNotificationPreference>
{
    public void Configure(EntityTypeBuilder<AccountNotificationPreference> b)
    {
        b.ToTable("notification_preferences");
        b.HasKey(x => new { x.UserId, x.GroupKey });
        b.Property(x => x.GroupKey).HasMaxLength(32).IsRequired();
        b.Property(x => x.Enabled).HasDefaultValue(true).IsRequired();
        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
