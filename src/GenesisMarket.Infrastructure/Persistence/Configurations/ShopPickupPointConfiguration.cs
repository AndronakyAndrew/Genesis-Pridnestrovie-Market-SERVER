using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public sealed class ShopPickupPointConfiguration : IEntityTypeConfiguration<ShopPickupPoint>
{
    public void Configure(EntityTypeBuilder<ShopPickupPoint> b)
    {
        b.ToTable("shop_pickup_points", t =>
        {
            t.HasCheckConstraint("ck_shop_pickup_points_address", "char_length(btrim(\"Address\")) BETWEEN 1 AND 200");
            t.HasCheckConstraint("ck_shop_pickup_points_note", "\"Note\" IS NULL OR char_length(\"Note\") <= 200");
            t.HasCheckConstraint("ck_shop_pickup_points_main_active", "NOT \"IsMain\" OR \"IsActive\"");
            t.HasCheckConstraint("ck_shop_pickup_points_sort_order", "\"SortOrder\" >= 0");
        });

        b.HasKey(x => x.Id);
        b.Property(x => x.City).HasMaxLength(CatalogReferenceConfiguration.CodeMaxLength).IsRequired();
        b.Property(x => x.Address).HasMaxLength(200).IsRequired();
        b.Property(x => x.Note).HasMaxLength(200);
        b.Property(x => x.Hours).HasColumnType("jsonb");
        b.Property(x => x.UpdatedAt).IsConcurrencyToken();

        b.HasOne<BusinessProfile>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<CatalogCity>()
            .WithMany()
            .HasForeignKey(x => x.City)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.OwnerId, x.SortOrder })
            .HasDatabaseName("ix_shop_pickup_points_owner_sort");
        b.HasIndex(x => x.OwnerId)
            .IsUnique()
            .HasFilter("\"IsMain\" = TRUE")
            .HasDatabaseName("ux_shop_pickup_points_one_main");
    }
}
