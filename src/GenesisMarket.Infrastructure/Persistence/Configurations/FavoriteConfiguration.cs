using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class FavoriteConfiguration : IEntityTypeConfiguration<Favorite>
{
    public void Configure(EntityTypeBuilder<Favorite> b)
    {
        b.ToTable("favorites");

        // Составной PK — идемпотентность на уровне БД.
        b.HasKey(f => new { f.UserId, f.ListingId });

        b.HasOne(f => f.Listing)
            .WithMany()
            .HasForeignKey(f => f.ListingId)
            .OnDelete(DeleteBehavior.Cascade);

        // Связь с User настроена в UserConfiguration (u.Favorites).
        b.HasIndex(f => f.ListingId);
    }
}
