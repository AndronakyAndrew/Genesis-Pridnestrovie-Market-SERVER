using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Infrastructure.Persistence;

/// <summary>
/// Единственный DbContext приложения. Изменение схемы — только миграцией EF Core.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<Subcategory> Subcategories => Set<Subcategory>();
    public DbSet<ListingImage> ListingImages => Set<ListingImage>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<VerificationCode> VerificationCodes => Set<VerificationCode>();
    public DbSet<SearchMiss> SearchMisses => Set<SearchMiss>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // pg_trgm — для fuzzy-fallback поиска по опечаткам (similarity()).
        modelBuilder.HasPostgresExtension("pg_trgm");

        // ---- Native enum-типы PostgreSQL (не строки, не числа) ----
        // Метки формирует LowerCaseNameTranslator. Тот же транслятор Npgsql
        // применяет при runtime-маппинге (auto-map из модели при UseNpgsql(строка)).
        var tr = LowerCaseNameTranslator.Instance;
        modelBuilder.HasPostgresEnum<Category>(name: "category", nameTranslator: tr);
        modelBuilder.HasPostgresEnum<City>(name: "city", nameTranslator: tr);
        modelBuilder.HasPostgresEnum<Condition>(name: "condition", nameTranslator: tr);
        modelBuilder.HasPostgresEnum<ListingStatus>(name: "listing_status", nameTranslator: tr);
        modelBuilder.HasPostgresEnum<PriceType>(name: "price_type", nameTranslator: tr);

        // Конфигурации сущностей — из этой же сборки.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
