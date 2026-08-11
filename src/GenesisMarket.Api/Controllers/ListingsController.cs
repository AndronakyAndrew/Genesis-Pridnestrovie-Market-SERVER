using System.Globalization;
using FluentValidation;
using FluentValidation.Results;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Жизненный цикл объявления: создание (черновик/публикация), карточка,
/// редактирование (владелец), снятие с публикации, отправка на публикацию,
/// свои объявления. Наружу — только DTO.
/// </summary>
public class ListingsController(
    AppDbContext db,
    IPublishingPolicy publishing,
    IAuthorizationService authorization,
    IListingModerationPolicy moderation,
    IListingViewCounter viewCounter,
    IValidator<CreateListingRequest> createValidator,
    IValidator<UpdateListingRequest> updateValidator,
    IMemoryCache cache,
    IOptions<ListingOptions> options) : ApiControllerBase
{
    // Статусы «в обороте» — учитываются в лимите и проверке дубликатов.
    private static readonly ListingStatus[] InCirculation =
        [ListingStatus.Active, ListingStatus.PendingReview];

    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;
    private const int MaxCities = 7;                       // столько городов в ПМР
    private const int MaxQueryLength = 100;               // предел длины поискового q
    private static readonly TimeSpan CountCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Каталог: только Active, курсорная пагинация, сортировка из белого списка.
    /// При <c>q</c> — полнотекстовый поиск (см. <see cref="SearchAsync"/>).
    /// Отдаёт витринную карточку (без телефона/email/UserId владельца).
    /// </summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<CatalogPageResponse>> GetAll(
        [FromQuery] CatalogQuery query, CancellationToken ct)
    {
        if (ValidateFilters(query) is { } bad)
            return bad;

        var q = NormalizeQuery(query.Q);

        if (CatalogQueryBuilder.ParseSort(query.Sort) is not { } sort)
            return Problem(title: "Неизвестная сортировка", statusCode: StatusCodes.Status400BadRequest);

        // Дефолт сортировки при поиске — по релевантности; relevance без q бессмысленна.
        if (q is not null && string.IsNullOrEmpty(query.Sort))
            sort = CatalogSort.Relevance;
        if (sort == CatalogSort.Relevance && q is null)
            return Problem(title: "Сортировка relevance требует параметра q",
                statusCode: StatusCodes.Status400BadRequest);

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        if (q is not null)
            return await SearchAsync(query, q, sort, limit, ct);

        // ---- Обычный каталог (без q) ----
        var listings = CatalogQueryBuilder.Filter(db.Listings.AsNoTracking(), query);

        if (query.Cursor is { Length: > 0 } cursor)
        {
            if (!CatalogCursor.TryDecode(cursor, CatalogQueryBuilder.Token(sort), out var key, out var afterId))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);
            listings = CatalogQueryBuilder.ApplyKeyset(listings, sort, key, afterId);
        }

        // Тянем на одну запись больше лимита — так узнаём hasMore без отдельного запроса.
        var rows = await CatalogQueryBuilder
            .Project(CatalogQueryBuilder.ApplyOrder(listings, sort).Take(limit + 1))
            .ToListAsync(ct);

        return Ok(BuildPage(rows, sort, limit, items: rows.Take(limit).Select(r => r.ToCard()).ToList()));
    }

    /// <summary>
    /// Полнотекстовый поиск: websearch_to_tsquery('russian', q) по SearchVector + все фильтры.
    /// Сортировка relevance (ts_rank_cd) или любая из белого списка. Если FTS дал 0 на первой
    /// странице — fuzzy-fallback по опечаткам (pg_trgm), а окончательный ноль — в SearchMisses.
    /// </summary>
    private async Task<ActionResult<CatalogPageResponse>> SearchAsync(
        CatalogQuery query, string q, CatalogSort sort, int limit, CancellationToken ct)
    {
        var filtered = CatalogQueryBuilder.ApplyTextSearch(
            CatalogQueryBuilder.Filter(db.Listings.AsNoTracking(), query), q);

        var isFirstPage = query.Cursor is not { Length: > 0 };
        if (!isFirstPage)
        {
            if (!CatalogCursor.TryDecode(query.Cursor!, CatalogQueryBuilder.Token(sort), out var key, out var afterId))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);

            if (sort == CatalogSort.Relevance)
            {
                if (!float.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out var rank))
                    return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);
                filtered = CatalogQueryBuilder.ApplyRelevanceKeyset(filtered, q, rank, afterId);
            }
            else
            {
                filtered = CatalogQueryBuilder.ApplyKeyset(filtered, sort, key, afterId);
            }
        }

        var projected = sort == CatalogSort.Relevance
            ? CatalogQueryBuilder.ProjectWithRank(CatalogQueryBuilder.ApplyRelevanceOrder(filtered, q).Take(limit + 1), q)
            : CatalogQueryBuilder.Project(CatalogQueryBuilder.ApplyOrder(filtered, sort).Take(limit + 1));

        var rows = await projected.ToListAsync(ct);

        // FTS вернул 0 на первой странице → отдельный fuzzy-режим (не смешиваем с FTS).
        if (rows.Count == 0 && isFirstPage)
            return await TrigramFallbackAsync(query, q, limit, ct);

        var page = rows.Take(limit).ToList();
        var items = await HighlightAsync(page, q, ct);
        return Ok(BuildPage(rows, sort, limit, items));
    }

    /// <summary>Fuzzy-поиск по опечаткам, когда FTS ничего не нашёл. Без keyset (только первая страница).</summary>
    private async Task<ActionResult<CatalogPageResponse>> TrigramFallbackAsync(
        CatalogQuery query, string q, int limit, CancellationToken ct)
    {
        var rows = await CatalogQueryBuilder
            .Project(CatalogQueryBuilder.ApplyTrigramFallback(
                CatalogQueryBuilder.Filter(db.Listings.AsNoTracking(), query), q).Take(limit))
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            // Ни FTS, ни fuzzy — это пробел в каталоге, логируем для наполнения.
            db.SearchMisses.Add(new SearchMiss { Query = q });
            await db.SaveChangesAsync(ct);
        }

        var items = await HighlightAsync(rows, q, ct);
        return Ok(new CatalogPageResponse(items, NextCursor: null, HasMore: false));
    }

    /// <summary>Достраивает подсвеченный заголовок (ts_headline) к карточкам страницы.</summary>
    private async Task<List<ListingCardResponse>> HighlightAsync(List<CatalogRow> rows, string q, CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        var highlights = await SearchHighlighter.HighlightTitlesAsync(
            db, rows.Select(r => r.Id).ToList(), q, ct);

        return rows
            .Select(r => r.ToCard() with { TitleHighlight = highlights.GetValueOrDefault(r.Id) })
            .ToList();
    }

    private static CatalogPageResponse BuildPage(
        List<CatalogRow> rows, CatalogSort sort, int limit, List<ListingCardResponse> items)
    {
        var hasMore = rows.Count > limit;
        // Курсор нужен только когда есть следующая страница; тогда rows[limit-1] заведомо существует.
        var nextCursor = hasMore
            ? CatalogCursor.Encode(
                CatalogQueryBuilder.Token(sort),
                CatalogQueryBuilder.NextKey(sort, rows[limit - 1]),
                rows[limit - 1].Id)
            : null;
        return new CatalogPageResponse(items, nextCursor, hasMore);
    }

    /// <summary>Trim + нижний регистр + обрезка до 100 символов. Пусто ⇒ null (поиска нет).</summary>
    private static string? NormalizeQuery(string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return null;
        var trimmed = q.Trim();
        if (trimmed.Length > MaxQueryLength)
            trimmed = trimmed[..MaxQueryLength];
        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Общее количество по тем же фильтрам, что и каталог. Точный COUNT дорог,
    /// поэтому результат кэшируется на 60 секунд по набору фильтров.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("count")]
    public async Task<ActionResult<ListingCountResponse>> Count(
        [FromQuery] CatalogQuery query, CancellationToken ct)
    {
        if (ValidateFilters(query) is { } bad)
            return bad;

        var cacheKey = CountCacheKey(query);
        if (!cache.TryGetValue(cacheKey, out long total))
        {
            total = await CatalogQueryBuilder.Filter(db.Listings.AsNoTracking(), query).LongCountAsync(ct);
            cache.Set(cacheKey, total, CountCacheTtl);
        }

        return Ok(new ListingCountResponse(total));
    }

    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ListingResponse>> GetById(Guid id, CancellationToken ct)
    {
        var listing = await db.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        if (listing.Status == ListingStatus.Active)
            await viewCounter.RegisterAsync(listing.Id, ClientIp(), ct);

        return Ok(Map(listing));
    }

    [AllowAnonymous]
    [HttpGet("by-slug/{slug}")]
    public async Task<ActionResult<ListingResponse>> GetBySlug(string slug, CancellationToken ct)
    {
        var listing = await db.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Slug == slug, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        if (listing.Status == ListingStatus.Active)
            await viewCounter.RegisterAsync(listing.Id, ClientIp(), ct);

        return Ok(Map(listing));
    }

    [Authorize]
    [HttpPost]
    public async Task<ActionResult<ListingResponse>> Create(CreateListingRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var validation = await createValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Invalid(validation);

        if (!await SubcategoryValidAsync(request.Category, request.SubcategoryId, ct))
            return Problem(title: "Подкатегория не найдена или не соответствует категории",
                statusCode: StatusCodes.Status400BadRequest);

        if (await DuplicateExistsAsync(userId, request.Category, request.Title, ct))
            return Problem(title: "У вас уже есть объявление с таким названием в этой категории",
                statusCode: StatusCodes.Status409Conflict);

        var listing = new Listing
        {
            Slug = "", // проставим при сохранении (нужен Id)
            Title = request.Title,
            Description = request.Description,
            Price = request.Price,
            PriceType = request.PriceType,
            Category = request.Category,
            SubcategoryId = request.SubcategoryId,
            City = request.City,
            District = request.District,
            Condition = request.Condition,
            Status = ListingStatus.Draft,
            OwnerId = userId
        };

        if (request.Publish)
        {
            if (await PublishGuardAsync(userId, ct) is { } guard)
                return guard;
            listing.Status = await moderation.ResolveOnPublishAsync(userId, ct);
            listing.PublishedAt = DateTimeOffset.UtcNow;
        }

        await SaveNewWithSlugAsync(listing, ct);
        return CreatedAtAction(nameof(GetById), new { id = listing.Id }, Map(listing));
    }

    [Authorize]
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<ListingResponse>> Update(
        Guid id, UpdateListingRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        // Только владелец. Фильтр по OwnerId ⇒ «нет объекта» и «чужой» дают 404.
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        var validation = await updateValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Invalid(validation);

        if (!await SubcategoryValidAsync(request.Category, request.SubcategoryId, ct))
            return Problem(title: "Подкатегория не найдена или не соответствует категории",
                statusCode: StatusCodes.Status400BadRequest);

        // Обновляем только редактируемые поля. Status/Slug/ViewsCount/PublishedAt не трогаем.
        listing.Title = request.Title;
        listing.Description = request.Description;
        listing.Price = request.Price;
        listing.PriceType = request.PriceType;
        listing.Category = request.Category;
        listing.SubcategoryId = request.SubcategoryId;
        listing.City = request.City;
        listing.District = request.District;
        listing.Condition = request.Condition;
        listing.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(Map(listing));
    }

    [Authorize]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        // Снять с публикации может владелец или модератор (существование публично → 403).
        var result = await authorization.AuthorizeAsync(User, listing, ResourceOwnerRequirement.Policy);
        if (!result.Succeeded)
            return Forbid();

        listing.Status = ListingStatus.Archived;
        listing.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    [Authorize]
    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<ListingResponse>> Publish(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        if (listing.Status != ListingStatus.Draft)
            return Problem(title: "Объявление уже отправлено на публикацию",
                statusCode: StatusCodes.Status409Conflict);

        if (await PublishGuardAsync(userId, ct) is { } guard)
            return guard;

        listing.Status = await moderation.ResolveOnPublishAsync(userId, ct);
        listing.PublishedAt = DateTimeOffset.UtcNow;
        listing.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(Map(listing));
    }

    [Authorize]
    [HttpGet("~/api/me/listings")]
    public async Task<ActionResult<IReadOnlyList<ListingResponse>>> MyListings(
        [FromQuery] ListingStatus? status, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var query = db.Listings.AsNoTracking().Where(l => l.OwnerId == userId);
        if (status is { } s)
            query = query.Where(l => l.Status == s);

        var items = await query
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => Map(l))
            .ToListAsync(ct);

        return Ok(items);
    }

    // ---- helpers ----

    /// <summary>Общие проверки фильтров каталога (для /listings и /listings/count). null — ок.</summary>
    private ObjectResult? ValidateFilters(CatalogQuery query)
    {
        if (query.Cities is { Count: > MaxCities })
            return Problem(title: $"Слишком много городов в фильтре (максимум {MaxCities})",
                statusCode: StatusCodes.Status400BadRequest);

        if (query.PriceFrom is { } from && query.PriceTo is { } to && from > to)
            return Problem(title: "priceFrom не может быть больше priceTo",
                statusCode: StatusCodes.Status400BadRequest);

        return null;
    }

    /// <summary>Стабильный ключ кэша количества по набору фильтров (сорт/курсор/лимит не влияют).</summary>
    private static string CountCacheKey(CatalogQuery q)
    {
        var cities = q.Cities is { Count: > 0 }
            ? string.Join('.', q.Cities.Select(c => (int)c).OrderBy(c => c))
            : "-";
        return string.Join('|',
            "count",
            q.Category?.ToString() ?? "-",
            q.Subcategory?.ToString() ?? "-",
            cities,
            q.PriceFrom?.ToString() ?? "-",
            q.PriceTo?.ToString() ?? "-",
            q.Condition?.ToString() ?? "-",
            q.PriceType?.ToString() ?? "-");
    }

    /// <summary>Проверки при публикации: подтверждённый контакт + лимит «в обороте». null — ок.</summary>
    private async Task<ObjectResult?> PublishGuardAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        var (canPublish, reason) = publishing.CanPublish(user);
        if (!canPublish)
            return Problem(title: reason, statusCode: StatusCodes.Status403Forbidden);

        var inCirculation = await db.Listings
            .CountAsync(l => l.OwnerId == userId && InCirculation.Contains(l.Status), ct);
        if (inCirculation >= options.Value.MaxActivePerUser)
            return Problem(title: "Достигнут лимит активных объявлений, архивируйте лишние",
                statusCode: StatusCodes.Status409Conflict);

        return null;
    }

    private Task<bool> SubcategoryValidAsync(Category category, int subcategoryId, CancellationToken ct) =>
        db.Subcategories.AnyAsync(s => s.Id == subcategoryId && s.Category == category, ct);

    private async Task<bool> DuplicateExistsAsync(Guid userId, Category category, string title, CancellationToken ct)
    {
        var normalized = ListingRules.NormalizeTitle(title);
        var titles = await db.Listings
            .Where(l => l.OwnerId == userId && l.Category == category && InCirculation.Contains(l.Status))
            .Select(l => l.Title)
            .ToListAsync(ct);
        return titles.Any(t => ListingRules.NormalizeTitle(t) == normalized);
    }

    /// <summary>Сохранение нового объявления с генерацией slug и повтором при коллизии.</summary>
    private async Task SaveNewWithSlugAsync(Listing listing, CancellationToken ct)
    {
        db.Listings.Add(listing);
        listing.Slug = SlugGenerator.Generate(listing.Title, SlugGenerator.SuffixFromId(listing.Id));

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex)
                when (attempt < 3 && ex.InnerException is PostgresException { SqlState: "23505" })
            {
                // Коллизия уникального slug — перегенерируем со случайным суффиксом.
                listing.Slug = SlugGenerator.Generate(listing.Title, SlugGenerator.RandomSuffix());
            }
        }
    }

    private ActionResult Invalid(ValidationResult validation)
    {
        foreach (var error in validation.Errors)
            ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
        return ValidationProblem(ModelState);
    }

    private static ListingResponse Map(Listing l) => new(
        l.Id, l.Slug, l.Title, l.Description, l.Price, l.PriceType, l.Category,
        l.SubcategoryId, l.City, l.District, l.Condition, l.Status,
        l.ViewsCount, l.OwnerId, l.CreatedAt, l.PublishedAt);
}
