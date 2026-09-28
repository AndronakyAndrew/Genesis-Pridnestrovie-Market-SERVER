using GenesisMarket.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GenesisMarket.Infrastructure.Persistence.Configurations;

/// <summary>
/// Справочники каталога: города и категории. Код — первичный ключ и значение в API;
/// сид начального набора — в миграции CatalogReferencesToTables (не HasData: данными
/// владеет администратор, и правка сида в коде не должна перезаписывать его изменения).
/// </summary>
public static class CatalogReferenceConfiguration
{
    public const int CodeMaxLength = 40;

    public static void Configure<T>(EntityTypeBuilder<T> b, string table) where T : CatalogReference
    {
        b.ToTable(table, t =>
        {
            t.HasCheckConstraint($"ck_{table}_code", "\"Code\" ~ '^[A-Z][A-Za-z0-9]{1,39}$'");
            t.HasCheckConstraint($"ck_{table}_slug", "\"Slug\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
            t.HasCheckConstraint($"ck_{table}_name", "char_length(btrim(\"Name\")) BETWEEN 2 AND 60");
        });

        b.HasKey(r => r.Code);
        b.Property(r => r.Code).HasMaxLength(CodeMaxLength);
        b.Property(r => r.Name).HasMaxLength(60).IsRequired();
        b.Property(r => r.Slug).HasMaxLength(60).IsRequired();
        b.Property(r => r.IsActive).HasDefaultValue(true);

        b.HasIndex(r => r.Slug).IsUnique();
        // Регистр кода в API не важен («tiraspol» == «Tiraspol»), поэтому коды, различающиеся
        // только регистром, запрещены уникальным индексом по lower("Code") — он создаётся
        // SQL-ом в миграции: индекс по выражению EF не описывает.
    }
}

public class CatalogCityConfiguration : IEntityTypeConfiguration<CatalogCity>
{
    public void Configure(EntityTypeBuilder<CatalogCity> b) => CatalogReferenceConfiguration.Configure(b, "cities");
}

public class CatalogCategoryConfiguration : IEntityTypeConfiguration<CatalogCategory>
{
    public void Configure(EntityTypeBuilder<CatalogCategory> b) => CatalogReferenceConfiguration.Configure(b, "categories");
}
