using System.Text;
using System.Xml;
using GenesisMarket.Api.Seo;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Карта сайта и robots для поисковиков. Всё на корне (не под /api). Объявлений может быть
/// сотни тысяч, поэтому XML генерируется потоково прямо в тело ответа — без материализации
/// всего списка в память. Ответы кэшируются на час (Cache-Control).
/// </summary>
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public class SitemapController(
    AppDbContext db,
    IMemoryCache cache,
    IOptions<SeoOptions> options) : ControllerBase
{
    private readonly SeoOptions _seo = options.Value;

    private const string Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private const string ActiveCountCacheKey = "seo:sitemap:active-count";
    private static readonly TimeSpan CountCacheTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// robots.txt: закрываем приватные/служебные API (модерация, кабинет, авторизация) и
    /// указываем адрес sitemap. Каталог и карточки объявлений остаются открыты для индексации.
    /// </summary>
    [HttpGet("/robots.txt")]
    public IActionResult Robots()
    {
        var sb = new StringBuilder();
        sb.Append("User-agent: *\n");
        sb.Append("Disallow: /api/moderation/\n");
        sb.Append("Disallow: /api/me/\n");
        sb.Append("Disallow: /api/auth/\n");

        if (_seo.NormalizedBaseUrl is { } baseUrl)
            sb.Append('\n').Append("Sitemap: ").Append(SeoUrls.Sitemap(baseUrl)).Append('\n');

        return Content(sb.ToString(), "text/plain; charset=utf-8");
    }

    /// <summary>
    /// Точка входа карты сайта. Пока URL немного (≤ порога) — один &lt;urlset&gt; со статикой и
    /// всеми объявлениями. Когда объявлений становится больше — превращается в sitemap-index
    /// с разбивкой на файлы по <see cref="SeoOptions.SitemapPageSize"/> ссылок.
    /// </summary>
    [HttpGet("/sitemap.xml")]
    public async Task<IActionResult> Sitemap(CancellationToken ct)
    {
        if (_seo.NormalizedBaseUrl is not { } baseUrl)
            return SeoUnavailable();

        var activeCount = await ActiveCountAsync(ct);
        var total = StaticUrlCount + activeCount;

        SetCacheHeaders();
        return total <= _seo.SitemapSplitThreshold
            ? await WriteFullSitemapAsync(baseUrl, ct)
            : await WriteSitemapIndexAsync(baseUrl, activeCount, ct);
    }

    /// <summary>Статические URL карты сайта: главная, все категории, все города.</summary>
    [HttpGet("/sitemap-static.xml")]
    public async Task<IActionResult> SitemapStatic(CancellationToken ct)
    {
        if (_seo.NormalizedBaseUrl is not { } baseUrl)
            return SeoUnavailable();

        SetCacheHeaders();
        await WriteUrlSetAsync(async writer =>
        {
            await WriteStaticUrlsAsync(writer, baseUrl);
        }, ct);
        return new EmptyResult();
    }

    /// <summary>
    /// Одна страница карты сайта с объявлениями (номер с 1). Объявления упорядочены по Id
    /// (UUIDv7 ≈ по времени), выборка страницы транслируется в SQL и стримится в ответ.
    /// </summary>
    [HttpGet("/sitemap-listings-{page:int}.xml")]
    public async Task<IActionResult> SitemapListings(int page, CancellationToken ct)
    {
        if (_seo.NormalizedBaseUrl is not { } baseUrl)
            return SeoUnavailable();

        if (page < 1)
            return NotFound();

        var activeCount = await ActiveCountAsync(ct);
        var pageCount = (int)Math.Max(1, Math.Ceiling(activeCount / (double)_seo.SitemapPageSize));
        if (page > pageCount)
            return NotFound();

        var query = db.Listings.AsNoTracking()
            .Where(l => l.Status == ListingStatus.Active)
            .OrderBy(l => l.Id)
            .Skip((page - 1) * _seo.SitemapPageSize)
            .Take(_seo.SitemapPageSize)
            .Select(l => new SitemapRow(l.Slug, l.UpdatedAt ?? l.PublishedAt ?? l.CreatedAt));

        SetCacheHeaders();
        await WriteUrlSetAsync(async writer =>
        {
            await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct))
                await WriteUrlAsync(writer, SeoUrls.Listing(baseUrl, row.Slug), row.LastMod);
        }, ct);
        return new EmptyResult();
    }

    // ---- запись XML ----

    private async Task<IActionResult> WriteFullSitemapAsync(string baseUrl, CancellationToken ct)
    {
        var query = db.Listings.AsNoTracking()
            .Where(l => l.Status == ListingStatus.Active)
            .OrderBy(l => l.Id)
            .Select(l => new SitemapRow(l.Slug, l.UpdatedAt ?? l.PublishedAt ?? l.CreatedAt));

        await WriteUrlSetAsync(async writer =>
        {
            await WriteStaticUrlsAsync(writer, baseUrl);
            await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct))
                await WriteUrlAsync(writer, SeoUrls.Listing(baseUrl, row.Slug), row.LastMod);
        }, ct);
        return new EmptyResult();
    }

    private async Task<IActionResult> WriteSitemapIndexAsync(string baseUrl, long activeCount, CancellationToken ct)
    {
        var pageCount = (int)Math.Ceiling(activeCount / (double)_seo.SitemapPageSize);

        var writer = CreateWriter();
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteStartDocumentAsync();
            await writer.WriteStartElementAsync(null, "sitemapindex", Ns);

            await WriteSitemapRefAsync(writer, $"{baseUrl}/sitemap-static.xml");
            for (var page = 1; page <= pageCount; page++)
                await WriteSitemapRefAsync(writer, $"{baseUrl}/sitemap-listings-{page}.xml");

            await writer.WriteEndElementAsync();
            await writer.WriteEndDocumentAsync();
            await writer.FlushAsync();
        }
        return new EmptyResult();
    }

    /// <summary>Открывает &lt;urlset&gt;, отдаёт запись тела делегату, закрывает документ.</summary>
    private async Task WriteUrlSetAsync(Func<XmlWriter, Task> writeBody, CancellationToken ct)
    {
        var writer = CreateWriter();
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteStartDocumentAsync();
            await writer.WriteStartElementAsync(null, "urlset", Ns);
            await writeBody(writer);
            await writer.WriteEndElementAsync();
            await writer.WriteEndDocumentAsync();
            await writer.FlushAsync();
        }
    }

    private XmlWriter CreateWriter()
    {
        Response.ContentType = "application/xml; charset=utf-8";
        return XmlWriter.Create(Response.Body, new XmlWriterSettings
        {
            Async = true,
            Indent = false,
            Encoding = new UTF8Encoding(false)
        });
    }

    private async Task WriteStaticUrlsAsync(XmlWriter writer, string baseUrl)
    {
        await WriteUrlAsync(writer, SeoUrls.Home(baseUrl), lastMod: null);
        foreach (var category in Enum.GetValues<Category>())
            await WriteUrlAsync(writer, SeoUrls.Category(baseUrl, category), lastMod: null);
        foreach (var city in Enum.GetValues<City>())
            await WriteUrlAsync(writer, SeoUrls.City(baseUrl, city), lastMod: null);
    }

    private static async Task WriteUrlAsync(XmlWriter writer, string loc, DateTimeOffset? lastMod)
    {
        await writer.WriteStartElementAsync(null, "url", Ns);
        await writer.WriteElementStringAsync(null, "loc", Ns, loc);
        if (lastMod is { } m)
            await writer.WriteElementStringAsync(null, "lastmod", Ns, m.UtcDateTime.ToString("yyyy-MM-dd"));
        await writer.WriteEndElementAsync();
    }

    private static async Task WriteSitemapRefAsync(XmlWriter writer, string loc)
    {
        await writer.WriteStartElementAsync(null, "sitemap", Ns);
        await writer.WriteElementStringAsync(null, "loc", Ns, loc);
        await writer.WriteEndElementAsync();
    }

    // ---- helpers ----

    /// <summary>Число статических URL: главная + категории + города.</summary>
    private static int StaticUrlCount =>
        1 + Enum.GetValues<Category>().Length + Enum.GetValues<City>().Length;

    /// <summary>Число активных объявлений с кэшем на час — точный COUNT дорог на каждый обход краулера.</summary>
    private async Task<long> ActiveCountAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(ActiveCountCacheKey, out long cached))
            return cached;

        var count = await db.Listings.AsNoTracking()
            .LongCountAsync(l => l.Status == ListingStatus.Active, ct);
        cache.Set(ActiveCountCacheKey, count, CountCacheTtl);
        return count;
    }

    private void SetCacheHeaders() =>
        Response.Headers.CacheControl = $"public, max-age={_seo.SitemapCacheSeconds}";

    private IActionResult SeoUnavailable() =>
        Problem(title: "SEO не настроен: не задан публичный адрес сайта (Seo:WebBaseUrl)",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>Строка карты сайта: slug для ссылки и дата последнего изменения.</summary>
    private readonly record struct SitemapRow(string Slug, DateTimeOffset LastMod);
}
