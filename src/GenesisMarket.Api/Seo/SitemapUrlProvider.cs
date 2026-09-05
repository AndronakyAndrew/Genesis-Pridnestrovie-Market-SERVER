using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Seo;

/// <summary>
/// Список URL карты сайта из БД и справочников. Из объявлений читаются ровно два поля —
/// slug (для адреса) и дата последнего изменения; ничего сверх публичного представления
/// в выборку не попадает. Берутся только <see cref="ListingStatus.Active"/>: проданные,
/// архивные, черновики, отклонённые и мягко удалённые (глобальный query filter) — мимо.
/// </summary>
public sealed class SitemapUrlProvider(AppDbContext db, IOptions<SeoOptions> options) : ISitemapUrlProvider
{
    private readonly SeoOptions _seo = options.Value;

    /// <summary>Главная и каталог — вершина сайта; фильтры каталога — витрины; инфо-страницы — низ.</summary>
    private const decimal TopPriority = 1.0m;
    private const decimal ShowcasePriority = 0.8m;
    private const decimal ListingPriority = 0.6m;
    private const decimal InfoPriority = 0.5m;

    public int StaticUrlCount =>
        2 + Enum.GetValues<Category>().Length + Enum.GetValues<City>().Length + SeoUrls.InfoPaths.Length;

    public int ListingPageCount(long activeCount) =>
        (int)Math.Max(1, Math.Ceiling(activeCount / (double)_seo.SitemapPageSize));

    public async Task<long> GetActiveListingCountAsync(CancellationToken ct) =>
        await ActiveListings().LongCountAsync(ct);

    public async Task<IReadOnlyList<SitemapUrl>> GetUrlsAsync(
        SitemapSection section, int page, CancellationToken ct)
    {
        var baseUrl = _seo.NormalizedBaseUrl
            ?? throw new InvalidOperationException("Seo:WebBaseUrl не задан — карту сайта строить не от чего");

        return section switch
        {
            SitemapSection.Static => StaticUrls(baseUrl),
            SitemapSection.Listings => await ListingUrlsAsync(baseUrl, page, ct),
            SitemapSection.All => [.. StaticUrls(baseUrl), .. await ListingUrlsAsync(baseUrl, page: null, ct)],
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        };
    }

    /// <summary>
    /// Статические публичные страницы: главная, каталог, каталог с фильтром по каждой
    /// категории и каждому городу, информационные разделы. Приватные страницы (кабинет,
    /// вход, создание объявления, модерация) сюда не добавляются — они в robots.txt.
    /// </summary>
    private static List<SitemapUrl> StaticUrls(string baseUrl)
    {
        var urls = new List<SitemapUrl>
        {
            new(SeoUrls.Home(baseUrl), null, SitemapChangeFreq.Weekly, TopPriority),
            new(SeoUrls.Catalog(baseUrl), null, SitemapChangeFreq.Weekly, TopPriority)
        };

        foreach (var category in Enum.GetValues<Category>())
            urls.Add(new SitemapUrl(
                SeoUrls.CatalogCategory(baseUrl, category), null, SitemapChangeFreq.Daily, ShowcasePriority));

        foreach (var city in Enum.GetValues<City>())
            urls.Add(new SitemapUrl(
                SeoUrls.CatalogCity(baseUrl, city), null, SitemapChangeFreq.Daily, ShowcasePriority));

        foreach (var path in SeoUrls.InfoPaths)
            urls.Add(new SitemapUrl($"{baseUrl}{path}", null, SitemapChangeFreq.Monthly, InfoPriority));

        return urls;
    }

    /// <summary>
    /// Объявления одной страницы карты (или все, если <paramref name="page"/> = null).
    /// Порядок — по Id: UUIDv7 монотонен по времени, поэтому нумерация страниц устойчива.
    /// Верхняя граница выборки — порог разбивки: даже если объявлений внезапно стало больше,
    /// файл не перевалит за лимит протокола (50 000 URL).
    /// </summary>
    private async Task<List<SitemapUrl>> ListingUrlsAsync(string baseUrl, int? page, CancellationToken ct)
    {
        var query = ActiveListings().OrderBy(l => l.Id);

        var rows = await (page is { } p
                ? query.Skip((p - 1) * _seo.SitemapPageSize).Take(_seo.SitemapPageSize)
                : query.Take(_seo.SitemapSplitThreshold))
            .Select(l => new { l.Slug, LastMod = l.UpdatedAt ?? l.PublishedAt ?? l.CreatedAt })
            .ToListAsync(ct);

        return rows
            .Select(r => new SitemapUrl(
                SeoUrls.Listing(baseUrl, r.Slug), r.LastMod, SitemapChangeFreq.Daily, ListingPriority))
            .ToList();
    }

    private IQueryable<Domain.Entities.Listing> ActiveListings() =>
        db.Listings.AsNoTracking().Where(l => l.Status == ListingStatus.Active);
}
