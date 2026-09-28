using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GenesisMarket.Api.Listings;

/// <summary>Снимок записи справочника для чтения (кэшируемый, без трекинга EF).</summary>
public sealed record CatalogEntry(string Code, string Name, string Slug, int SortOrder, bool IsActive);

public sealed record SubcategoryEntry(int Id, string Category, string Slug, string Name, bool IsActive);

/// <summary>Весь справочник каталога одним снимком.</summary>
public sealed class CatalogSnapshot
{
    public required IReadOnlyList<CatalogEntry> Cities { get; init; }
    public required IReadOnlyList<CatalogEntry> Categories { get; init; }
    public required IReadOnlyList<SubcategoryEntry> Subcategories { get; init; }

    private Dictionary<string, CatalogEntry>? _cities;
    private Dictionary<string, CatalogEntry>? _categories;

    private Dictionary<string, CatalogEntry> CityIndex =>
        _cities ??= Cities.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, CatalogEntry> CategoryIndex =>
        _categories ??= Categories.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    public CatalogEntry? City(string? code) =>
        code is not null && CityIndex.TryGetValue(code.Trim(), out var e) ? e : null;

    public CatalogEntry? Category(string? code) =>
        code is not null && CategoryIndex.TryGetValue(code.Trim(), out var e) ? e : null;

    /// <summary>Подпись города для текста, видимого людям; неизвестный код — как есть.</summary>
    public string CityName(string code) => City(code)?.Name ?? code;

    public string CategoryName(string code) => Category(code)?.Name ?? code;

    /// <summary>
    /// Код для записи нового значения: канонический, если город есть и включён. Выключенный
    /// допустим, только если это текущее значение объекта (<paramref name="current"/>) —
    /// правка объявления в выключенном городе не должна заставлять менять город. null — нельзя.
    /// </summary>
    public string? WritableCity(string? raw, string? current = null) => Writable(City(raw), current);

    public string? WritableCategory(string? raw, string? current = null) => Writable(Category(raw), current);

    private static string? Writable(CatalogEntry? entry, string? current) =>
        entry is null ? null
        : entry.IsActive || string.Equals(entry.Code, current, StringComparison.Ordinal) ? entry.Code
        : null;

    /// <summary>
    /// Фильтры каталога и сохранённых поисков → канонические коды. Выключенные допустимы
    /// (по ним ещё лежат объявления), неизвестные — ошибка, как раньше неизвестное
    /// значение enum давало 400. Возвращает текст ошибки или null.
    /// </summary>
    public string? NormalizeFilters(
        string? category, IReadOnlyList<string>? cities,
        out string? normalizedCategory, out List<string>? normalizedCities)
    {
        normalizedCategory = null;
        normalizedCities = null;

        if (!string.IsNullOrWhiteSpace(category))
        {
            normalizedCategory = Category(category)?.Code;
            if (normalizedCategory is null)
                return "Неизвестная категория";
        }

        if (cities is { Count: > 0 })
        {
            normalizedCities = new List<string>(cities.Count);
            foreach (var raw in cities)
            {
                if (City(raw)?.Code is not { } code)
                    return "Неизвестный город";
                normalizedCities.Add(code);
            }
            normalizedCities = normalizedCities.Distinct().ToList();
        }

        return null;
    }
}

/// <summary>
/// Справочники каталога (города, категории, подкатегории) из БД. Регистр кода на входе
/// не важен — как раньше у enum-ов («tiraspol» == «Tiraspol»); наружу и в БД всегда
/// уходит канонический код. Снимок кэшируется: справочник читается на каждом запросе
/// каталога, а меняется редко — после изменения администратор сбрасывает кэш.
/// </summary>
public interface ICatalogReferences
{
    Task<CatalogSnapshot> GetAsync(CancellationToken ct);

    /// <summary>Сбросить кэш после изменения справочника.</summary>
    void Invalidate();
}

public sealed class CatalogReferences(AppDbContext db, IMemoryCache cache) : ICatalogReferences
{
    private const string CacheKey = "catalog:references";

    /// <summary>
    /// Страховочный TTL: основной путь — явный Invalidate после изменения. TTL нужен на
    /// случай нескольких инстансов API, где сброс в одном не виден другим.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public async Task<CatalogSnapshot> GetAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            return new CatalogSnapshot
            {
                Cities = await Load(db.Cities, ct),
                Categories = await Load(db.Categories, ct),
                Subcategories = await db.Subcategories.AsNoTracking()
                    .OrderBy(s => s.Category).ThenBy(s => s.Name)
                    .Select(s => new SubcategoryEntry(s.Id, s.Category, s.Slug, s.Name, s.IsActive))
                    .ToListAsync(ct)
            };
        }))!;

    public void Invalidate() => cache.Remove(CacheKey);

    private static async Task<IReadOnlyList<CatalogEntry>> Load<T>(DbSet<T> set, CancellationToken ct)
        where T : CatalogReference =>
        await set.AsNoTracking()
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name)
            .Select(r => new CatalogEntry(r.Code, r.Name, r.Slug, r.SortOrder, r.IsActive))
            .ToListAsync(ct);
}
