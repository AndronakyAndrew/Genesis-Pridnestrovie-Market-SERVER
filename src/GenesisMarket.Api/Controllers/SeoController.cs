using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Seo;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Данные для индексации: готовые мета карточки объявления (title/description/og/JSON-LD) и
/// сводки для статических посадочных «категория × город». Всё публично (AllowAnonymous).
/// </summary>
[AllowAnonymous]
[Route("api")]
public class SeoController(
    AppDbContext db,
    IObjectStorage storage,
    IOptions<SeoOptions> options) : ApiControllerBase
{
    private readonly SeoOptions _seo = options.Value;

    /// <summary>Сколько подкатегорий показывать в сводке посадочной.</summary>
    private const int TopSubcategories = 8;

    /// <summary>
    /// SEO-мета карточки объявления. HTTP-коды намеренно различают судьбу URL для поисковика:
    /// удалённое — 410 Gone (убрать из индекса), снятое с публикации (архив/продано) — 200
    /// с <c>noindex</c> и <c>isArchived</c>, никогда не публиковавшееся или несуществующее — 404.
    /// </summary>
    [HttpGet("listings/{id:guid}/meta")]
    public async Task<ActionResult<ListingMetaResponse>> ListingMeta(Guid id, CancellationToken ct)
    {
        if (BaseUrl() is not { } baseUrl)
            return SeoUnavailable();

        // IgnoreQueryFilters — чтобы «увидеть» мягко удалённые (для ответа 410, а не 404).
        var row = await db.Listings.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.Id == id)
            .Select(l => new
            {
                l.Slug,
                l.Title,
                l.Description,
                l.Price,
                l.PriceType,
                l.Category,
                l.City,
                l.Condition,
                l.Status,
                l.DeletedAt,
                SellerName = l.Owner!.Profile!.DisplayName,
                FirstImageKey = l.Images.OrderBy(i => i.SortOrder).Select(i => i.ObjectKey).FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        // Удалено (soft-delete) → 410 Gone: поисковик должен убрать URL из индекса.
        if (row.DeletedAt is not null)
            return Problem(title: "Объявление удалено", statusCode: StatusCodes.Status410Gone);

        // Черновик / на премодерации / отклонённое — публичного URL никогда не было → 404.
        if (row.Status is ListingStatus.Draft or ListingStatus.PendingReview or ListingStatus.Rejected)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        var canonicalUrl = SeoUrls.Listing(baseUrl, row.Slug);
        var ogImage = row.FirstImageKey is null
            ? null
            : await storage.GetPresignedUrlAsync(
                row.FirstImageKey, TimeSpan.FromDays(_seo.OgImageTtlDays), ct);

        var meta = ListingMetaBuilder.Build(
            new ListingMetaBuilder.MetaInput(
                row.Title, row.Description, row.Price, row.PriceType, row.Category,
                row.City, row.Condition, row.Status, row.SellerName),
            canonicalUrl, ogImage, _seo.SiteName);

        return Ok(meta);
    }

    /// <summary>
    /// Сводка для посадочной «категория × город» (например, «Купить квартиру в Тирасполе»):
    /// число активных объявлений, диапазон цен и топ подкатегорий. Неизвестная пара — 404.
    /// </summary>
    [HttpGet("seo/landing/{category}/{city}")]
    public async Task<ActionResult<SeoLandingResponse>> Landing(
        string category, string city, CancellationToken ct)
    {
        if (BaseUrl() is not { } baseUrl)
            return SeoUnavailable();

        if (!SeoUrls.TryParse<Category>(category, out var cat) || !SeoUrls.TryParse<City>(city, out var cty))
            return Problem(title: "Неизвестная категория или город", statusCode: StatusCodes.Status404NotFound);

        var active = db.Listings.AsNoTracking()
            .Where(l => l.Status == ListingStatus.Active && l.Category == cat && l.City == cty);

        var count = await active.LongCountAsync(ct);

        // Диапазон цен считаем только по объявлениям с указанной ценой (договорные/бесплатные — мимо).
        var priced = active.Where(l => l.Price != null);
        var range = await priced
            .GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(l => l.Price), Max = g.Max(l => l.Price) })
            .FirstOrDefaultAsync(ct);

        var topRaw = await active
            .GroupBy(l => l.SubcategoryId)
            .Select(g => new { SubcategoryId = g.Key, Count = g.LongCount() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.SubcategoryId)
            .Take(TopSubcategories)
            .ToListAsync(ct);

        var names = await db.Subcategories.AsNoTracking()
            .Where(s => topRaw.Select(t => t.SubcategoryId).Contains(s.Id))
            .Select(s => new { s.Id, s.Slug, s.Name })
            .ToDictionaryAsync(s => s.Id, ct);

        var top = topRaw
            .Where(t => names.ContainsKey(t.SubcategoryId))
            .Select(t => new LandingSubcategoryResponse(
                t.SubcategoryId, names[t.SubcategoryId].Slug, names[t.SubcategoryId].Name, t.Count))
            .ToList();

        return Ok(new SeoLandingResponse(
            Category: SeoUrls.Value(cat),
            CategoryLabel: Listings.CatalogLabels.Category(cat),
            City: SeoUrls.Value(cty),
            CityLabel: Listings.CatalogLabels.City(cty),
            CanonicalUrl: SeoUrls.Landing(baseUrl, cat, cty),
            Count: count,
            PriceFrom: range?.Min,
            PriceTo: range?.Max,
            TopSubcategories: top));
    }

    private string? BaseUrl() => _seo.NormalizedBaseUrl;

    private ObjectResult SeoUnavailable() =>
        Problem(title: "SEO не настроен: не задан публичный адрес сайта (Seo:WebBaseUrl)",
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
