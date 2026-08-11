using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace GenesisMarket.Tests;

/// <summary>
/// Диагностика планов запросов каталога. НЕ трогает прод: использует временный
/// Postgres тест-фабрики (свой контейнер на класс), засевает реалистичный объём
/// и печатает EXPLAIN для трёх типовых комбинаций фильтров, проверяя, что
/// используются индексы из шага 1. Помечен Skip — запускать вручную, сняв Skip.
/// </summary>
public sealed class CatalogExplainDiagnostics(AuthApiFactory factory, ITestOutputHelper output)
    : IClassFixture<AuthApiFactory>
{
    [Fact(Skip = "Диагностика планов запросов (сеет 20k строк, ~13с); запускать вручную, сняв Skip.")]
    public async Task Explain_three_typical_filter_combinations()
    {
        await SeedAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Для каждой комбинации печатаем реальный SQL из билдера (ToQueryString)
        // и EXPLAIN ANALYZE на литеральном эквиваленте (EF-параметры Postgres в сыром
        // EXPLAIN не понимает, поэтому значения фильтров инлайним).

        // 1) Только статус + свежесть (дефолт каталога).
        await Explain(db, "1. Active + sort=new",
            CatalogQueryBuilder.ApplyOrder(
                CatalogQueryBuilder.Filter(db.Listings.AsNoTracking(), new CatalogQuery()), CatalogSort.New),
            """
            SELECT l."Id" FROM listings AS l
            WHERE l."DeletedAt" IS NULL AND l."Status" = 'active'
            ORDER BY l."CreatedAt" DESC, l."Id" DESC
            LIMIT 21
            """);

        // 2) Категория + город + свежесть.
        await Explain(db, "2. Active + category + city + sort=new",
            CatalogQueryBuilder.ApplyOrder(
                CatalogQueryBuilder.Filter(db.Listings.AsNoTracking(),
                    new CatalogQuery { Category = Category.Electronics, Cities = [City.Tiraspol] }), CatalogSort.New),
            """
            SELECT l."Id" FROM listings AS l
            WHERE l."DeletedAt" IS NULL AND l."Status" = 'active'
              AND l."Category" = 'electronics' AND l."City" = 'tiraspol'
            ORDER BY l."CreatedAt" DESC, l."Id" DESC
            LIMIT 21
            """);

        // 3) Категория + диапазон цены + сортировка по цене.
        await Explain(db, "3. Active + category + priceRange + sort=price_asc",
            CatalogQueryBuilder.ApplyOrder(
                CatalogQueryBuilder.Filter(db.Listings.AsNoTracking(),
                    new CatalogQuery { Category = Category.Electronics, PriceFrom = 1000, PriceTo = 5000 }),
                CatalogSort.PriceAsc),
            """
            SELECT l."Id" FROM listings AS l
            WHERE l."DeletedAt" IS NULL AND l."Status" = 'active'
              AND l."Category" = 'electronics'
              AND l."Price" IS NOT NULL AND l."Price" >= 1000 AND l."Price" <= 5000
            ORDER BY (l."Price" IS NULL), l."Price", l."Id"
            LIMIT 21
            """);
    }

    private async Task Explain(AppDbContext db, string label, IQueryable<Listing> builderQuery, string explainSql)
    {
        var efSql = builderQuery
            .Select(l => new { l.Id, l.Slug, l.Title, l.Price, l.City, l.Category, l.PublishedAt })
            .Take(21)
            .ToQueryString();

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var lines = new List<string>();
        await using (var cmd = new NpgsqlCommand($"EXPLAIN (ANALYZE, BUFFERS) {explainSql}", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                lines.Add(reader.GetString(0));

        output.WriteLine($"===== {label} =====");
        output.WriteLine("-- EF SQL (builder) --");
        output.WriteLine(efSql);
        output.WriteLine("-- EXPLAIN ANALYZE (literal) --");
        output.WriteLine(string.Join('\n', lines));
        output.WriteLine("");
    }

    private async Task SeedAsync()
    {
        var ownerId = await factory.SeedUserAsync($"explain-{Guid.NewGuid():N}@test.io", "CorrectHorse7");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rnd = new Random(42);
        var cities = Enum.GetValues<City>();
        var categories = Enum.GetValues<Category>();
        var statuses = new[]
        {
            ListingStatus.Active, ListingStatus.Active, ListingStatus.Active,
            ListingStatus.Sold, ListingStatus.Archived, ListingStatus.Draft
        };

        var batch = new List<Listing>();
        for (var i = 0; i < 20_000; i++)
        {
            batch.Add(new Listing
            {
                Slug = $"seed-{i}-{Guid.NewGuid():N}",
                Title = $"Объявление каталога номер {i}",
                Description = "Реалистичное описание объявления для оценки планов запроса",
                Price = rnd.Next(0, 100_000),
                PriceType = PriceType.Fixed,
                Category = categories[rnd.Next(categories.Length)],
                SubcategoryId = 18,
                City = cities[rnd.Next(cities.Length)],
                Condition = Condition.Used,
                Status = statuses[rnd.Next(statuses.Length)],
                PublishedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                OwnerId = ownerId
            });
            if (batch.Count == 2000)
            {
                db.Listings.AddRange(batch);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                batch.Clear();
            }
        }
        await db.Database.ExecuteSqlRawAsync("ANALYZE listings;");
    }
}
