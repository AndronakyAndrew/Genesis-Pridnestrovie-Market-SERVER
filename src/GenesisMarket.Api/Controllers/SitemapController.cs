using System.Text;
using GenesisMarket.Api.Seo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Карта сайта и robots для поисковиков. Всё на корне (не под /api), доступ анонимный.
/// Сборка списка URL — в <see cref="ISitemapUrlProvider"/>, разметка — в
/// <see cref="SitemapXml"/>, готовый ответ лежит в <see cref="SitemapCache"/>: обход
/// краулера не бьёт в БД чаще раза за TTL.
/// </summary>
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public class SitemapController(
    ISitemapUrlProvider urls,
    SitemapCache cache,
    IOptions<SeoOptions> options) : ControllerBase
{
    private readonly SeoOptions _seo = options.Value;

    private const string XmlContentType = "application/xml; charset=utf-8";

    /// <summary>
    /// robots.txt: закрываем приватные страницы сайта (кабинет, вход, создание объявления,
    /// модерация, профили) и служебные API, указываем адрес карты сайта. Отдаём динамически,
    /// а не файлом из wwwroot, потому что строка Sitemap зависит от <c>Seo:WebBaseUrl</c>.
    /// Учтите: robots.txt действует на тот хост, с которого отдан, — этот закрывает домен API;
    /// на домене фронтенда нужен свой robots.txt с теми же Disallow.
    /// </summary>
    [HttpGet("/robots.txt")]
    public IActionResult Robots()
    {
        var sb = new StringBuilder();
        sb.Append("User-agent: *\n");

        sb.Append("# приватные разделы сайта\n");
        foreach (var path in SeoUrls.DisallowedPaths)
            sb.Append("Disallow: ").Append(path).Append('\n');

        sb.Append("# служебные API\n");
        sb.Append("Disallow: /api/moderation/\n");
        sb.Append("Disallow: /api/me/\n");
        sb.Append("Disallow: /api/auth/\n");

        if (_seo.NormalizedBaseUrl is { } baseUrl)
            sb.Append('\n').Append("Sitemap: ").Append(SeoUrls.Sitemap(baseUrl)).Append('\n');

        return Content(sb.ToString(), "text/plain; charset=utf-8");
    }

    /// <summary>
    /// Точка входа карты сайта. Пока URL немного (≤ порога) — один &lt;urlset&gt; со статикой и
    /// всеми активными объявлениями. Когда объявлений становится больше — тот же адрес отдаёт
    /// sitemap-index со ссылками на sitemap-static.xml и sitemap-listings-{n}.xml
    /// (лимит протокола — 50 000 URL и 50 МБ на файл).
    /// </summary>
    [HttpGet("/sitemap.xml")]
    public async Task<IActionResult> Sitemap(CancellationToken ct)
    {
        if (_seo.NormalizedBaseUrl is not { } baseUrl)
            return SeoUnavailable();

        var activeCount = await ActiveCountAsync(ct);
        if (urls.StaticUrlCount + activeCount <= _seo.SitemapSplitThreshold)
            return await XmlAsync(SitemapSection.All, page: 0, ct);

        var index = await cache.GetOrCreateAsync(
            SitemapCache.IndexKey,
            _ => Task.FromResult(SitemapXml.RenderIndex(IndexLocations(baseUrl, activeCount))),
            ct);
        return Xml(index);
    }

    /// <summary>Статические публичные страницы: главная, каталог, витрины фильтров, инфо-разделы.</summary>
    [HttpGet("/sitemap-static.xml")]
    public async Task<IActionResult> SitemapStatic(CancellationToken ct) =>
        _seo.NormalizedBaseUrl is null
            ? SeoUnavailable()
            : await XmlAsync(SitemapSection.Static, page: 0, ct);

    /// <summary>Одна страница карты сайта с объявлениями (нумерация с 1).</summary>
    [HttpGet("/sitemap-listings-{page:int}.xml")]
    public async Task<IActionResult> SitemapListings(int page, CancellationToken ct)
    {
        if (_seo.NormalizedBaseUrl is null)
            return SeoUnavailable();

        if (page < 1)
            return NotFound();

        var activeCount = await ActiveCountAsync(ct);
        if (page > urls.ListingPageCount(activeCount))
            return NotFound();

        return await XmlAsync(SitemapSection.Listings, page, ct);
    }

    /// <summary>Ссылки sitemap-index: статика отдельным файлом, объявления — постранично.</summary>
    private List<string> IndexLocations(string baseUrl, long activeCount)
    {
        var locations = new List<string> { $"{baseUrl}/sitemap-static.xml" };
        for (var page = 1; page <= urls.ListingPageCount(activeCount); page++)
            locations.Add($"{baseUrl}/sitemap-listings-{page}.xml");
        return locations;
    }

    /// <summary>Готовый (закэшированный) XML раздела карты сайта.</summary>
    private async Task<IActionResult> XmlAsync(SitemapSection section, int page, CancellationToken ct)
    {
        var bytes = await cache.GetOrCreateAsync(
            SitemapCache.Key(section, page),
            async token => SitemapXml.RenderUrlSet(await urls.GetUrlsAsync(section, page, token)),
            ct);
        return Xml(bytes);
    }

    private IActionResult Xml(byte[] bytes)
    {
        Response.Headers.CacheControl = $"public, max-age={_seo.SitemapCacheSeconds}";
        return new FileContentResult(bytes, XmlContentType);
    }

    /// <summary>Число активных объявлений с тем же TTL: точный COUNT на каждый обход дорог.</summary>
    private Task<long> ActiveCountAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync(SitemapCache.CountKey, urls.GetActiveListingCountAsync, ct);

    private IActionResult SeoUnavailable() =>
        Problem(title: "SEO не настроен: не задан публичный адрес сайта (Seo:WebBaseUrl)",
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
