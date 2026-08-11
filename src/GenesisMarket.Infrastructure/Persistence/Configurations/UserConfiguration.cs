using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(u => u.Id);

        b.Property(u => u.Email).HasMaxLength(256).IsRequired();
        b.HasIndex(u => u.Email).IsUnique();

        b.Property(u => u.PasswordHash).HasMaxLength(200).IsRequired();

        // Role — не native enum, хранится строкой.
        b.Property(u => u.Role)
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(UserRole.User)
            .IsRequired();

        b.Property(u => u.PhoneE164).HasMaxLength(20);
        b.Property(u => u.PhoneVerified).HasDefaultValue(false);
        b.Property(u => u.IsBanned).HasDefaultValue(false);

        // Денормализованный рейтинг: пересчитывается триггером reviews_rating_sync.
        b.Property(u => u.ReviewsCount).HasDefaultValue(0);

        // Профиль 1:1 (owned-подобно, но отдельной таблицей profiles).
        b.HasOne(u => u.Profile)
            .WithOne(p => p.User)
            .HasForeignKey<Profile>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Удаление пользователя НЕ каскадит на объявления (вместо удаления —
        // анонимизация + архивация объявлений в прикладной логике).
        b.HasMany(u => u.Listings)
            .WithOne(l => l.Owner)
            .HasForeignKey(l => l.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(u => u.Favorites)
            .WithOne(f => f.User)
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
