using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

public class ListingConfiguration : IEntityTypeConfiguration<Listing>
{
    public void Configure(EntityTypeBuilder<Listing> b)
    {
        b.ToTable("listings", t =>
        {
            // Длина полей — на уровне БД, а не только атрибутами DTO.
            t.HasCheckConstraint(
                "ck_listings_title_length",
                "char_length(\"Title\") >= 5 AND char_length(\"Title\") <= 120");
            t.HasCheckConstraint(
                "ck_listings_description_length",
                "char_length(\"Description\") <= 5000");
            t.HasCheckConstraint(
                "ck_listings_district_length",
                "\"District\" IS NULL OR char_length(\"District\") <= 100");

            // Цена неотрицательна.
            t.HasCheckConstraint(
                "ck_listings_price_nonnegative",
                "\"Price\" IS NULL OR \"Price\" >= 0");

            // Согласованность цены и типа цены:
            // free → 0, negotiable → NULL, fixed → задана и ≥ 0.
            t.HasCheckConstraint(
                "ck_listings_price_pricetype",
                "(\"PriceType\" = 'free' AND \"Price\" = 0) " +
                "OR (\"PriceType\" = 'negotiable' AND \"Price\" IS NULL) " +
                "OR (\"PriceType\" = 'fixed' AND \"Price\" IS NOT NULL AND \"Price\" >= 0)");

            // Счётчик просмотров не бывает отрицательным.
            t.HasCheckConstraint(
                "ck_listings_views_nonnegative",
                "\"ViewsCount\" >= 0");
        });

        b.HasKey(l => l.Id);

        b.Property(l => l.Title).HasMaxLength(120).IsRequired();
        b.Property(l => l.Description).HasMaxLength(5000).IsRequired();
        b.Property(l => l.District).HasMaxLength(100);

        // Цена — целые рубли ПМР.
        b.Property(l => l.Price).HasColumnType("numeric(12,0)");

        // Category / City / Condition / Status / PriceType — native enum-типы.
        b.Property(l => l.Category).IsRequired();
        b.Property(l => l.City).IsRequired();
        b.Property(l => l.Condition).IsRequired();
        b.Property(l => l.Status).IsRequired();
        b.Property(l => l.PriceType).IsRequired();

        b.Property(l => l.ViewsCount).HasDefaultValue(0);

        // FK на справочник подкатегорий (не свободный текст).
        b.HasOne(l => l.Subcategory)
            .WithMany(s => s.Listings)
            .HasForeignKey(l => l.SubcategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // Будущий полнотекстовый вектор (шаг 7). Держим shadow-свойством,
        // чтобы Domain оставался без зависимости от Npgsql-типов.
        b.Property<NpgsqlTsVector?>("SearchVector");

        // ---- Индексы ----
        // Каталог по статусу и свежести (частичный: только «живые» объявления).
        b.HasIndex(l => new { l.Status, l.CreatedAt })
            .IsDescending(false, true)
            .HasFilter("\"DeletedAt\" IS NULL");

        // Фильтр каталога категория/город/статус.
        b.HasIndex(l => new { l.Category, l.City, l.Status })
            .HasFilter("\"DeletedAt\" IS NULL");

        // «Мои объявления» по статусу.
        b.HasIndex(l => new { l.OwnerId, l.Status });

        b.HasIndex(l => l.SubcategoryId);

        // GIN по будущему SearchVector.
        b.HasIndex("SearchVector").HasMethod("gin");

        // Мягкое удаление: глобальный фильтр скрывает удалённые записи.
        b.HasQueryFilter(l => l.DeletedAt == null);
    }
}
