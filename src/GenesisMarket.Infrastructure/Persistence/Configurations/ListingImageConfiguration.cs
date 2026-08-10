using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class ListingImageConfiguration : IEntityTypeConfiguration<ListingImage>
{
    public void Configure(EntityTypeBuilder<ListingImage> b)
    {
        b.ToTable("listing_images");
        b.HasKey(i => i.Id);

        b.Property(i => i.ObjectKey).HasMaxLength(512).IsRequired();
        b.Property(i => i.ThumbKey).HasMaxLength(512).IsRequired();

        // Каскадное удаление вместе с объявлением.
        b.HasOne(i => i.Listing)
            .WithMany(l => l.Images)
            .HasForeignKey(i => i.ListingId)
            .OnDelete(DeleteBehavior.Cascade);

        // Порядок изображений в пределах объявления.
        b.HasIndex(i => new { i.ListingId, i.SortOrder });
    }
}
