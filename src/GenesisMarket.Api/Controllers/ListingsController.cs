using System.Globalization;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Api.Outbox.Telegram;
using GenesisMarket.Api.Profiles;
using GenesisMarket.Api.Security;
using GenesisMarket.Api.Seo;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    IContactRevealService contactReveal,
    IPublicCodeResolver publicCodes,
    IValidator<CreateListingRequest> createValidator,
    IValidator<UpdateListingRequest> updateValidator,
    IMemoryCache cache,
    IOptions<ListingOptions> options,
    IOptions<CatalogHygieneOptions> hygiene,
    IOptions<SeoOptions> seo) : ApiControllerBase
{
    // Статусы «в обороте» — учитываются в лимите и проверке дубликатов.
    private static readonly ListingStatus[] InCirculation =
        [ListingStatus.Active, ListingStatus.PendingReview];

    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;
    private static readonly TimeSpan CountCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Каталог: только Active, курсорная пагинация, сортировка из белого списка.
    /// При <c>q</c> — полнотекстовый поиск (см. <see cref="SearchAsync"/>).
    /// Отдаёт витринную карточку (без телефона/email/UserId владельца).
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Search)]
    [HttpGet]
    public async Task<ActionResult<CatalogPageResponse>> GetAll(
        [FromQuery] CatalogQuery query, CancellationToken ct)
    {
        if (ValidateFilters(query) is { } bad)
            return bad;

        var q = CatalogQueryBuilder.NormalizeText(query.Q);

        if (CatalogQueryBuilder.ParseSort(query.Sort) is not { } sort)
            return Problem(title: "Неизвестная сортировка", statusCode: StatusCodes.Status400BadRequest);

        // Дефолт сортировки при поиске — по релевантности; relevance без q бессмысленна.
        if (q is not null && string.IsNullOrEmpty(query.Sort))
            sort = CatalogSort.Relevance;
        if (sort == CatalogSort.Relevance && q is null)
            return Problem(title: "Сортировка relevance требует параметра q",
                statusCode: StatusCodes.Status400BadRequest);

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        // Неизвестный продавец — пустая выдача, а не весь каталог.
        if (await CatalogSourceAsync(query, ct) is not { } source)
            return Ok(new CatalogPageResponse([], NextCursor: null, HasMore: false));

        if (q is not null)
            return await SearchAsync(source, query, q, sort, limit, ct);

        // ---- Обычный каталог (без q) ----
        var listings = CatalogQueryBuilder.Filter(source, query);

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

        return await OkPageAsync(
            BuildPage(rows, sort, limit, items: rows.Take(limit).Select(r => r.ToCard()).ToList()), ct);
    }

    /// <summary>
    /// Полнотекстовый поиск: websearch_to_tsquery('russian', q) по SearchVector + все фильтры.
    /// Сортировка relevance (ts_rank_cd) или любая из белого списка. Если FTS дал 0 на первой
    /// странице — fuzzy-fallback по опечаткам (pg_trgm), а окончательный ноль — в SearchMisses.
    /// </summary>
    private async Task<ActionResult<CatalogPageResponse>> SearchAsync(
        IQueryable<Listing> source, CatalogQuery query, string q, CatalogSort sort, int limit, CancellationToken ct)
    {
        var filtered = CatalogQueryBuilder.ApplyTextSearch(
            CatalogQueryBuilder.Filter(source, query), q);

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
            return await TrigramFallbackAsync(source, query, q, limit, ct);

        var page = rows.Take(limit).ToList();
        var items = await HighlightAsync(page, q, ct);
        return await OkPageAsync(BuildPage(rows, sort, limit, items), ct);
    }

    /// <summary>Fuzzy-поиск по опечаткам, когда FTS ничего не нашёл. Без keyset (только первая страница).</summary>
    private async Task<ActionResult<CatalogPageResponse>> TrigramFallbackAsync(
        IQueryable<Listing> source, CatalogQuery query, string q, int limit, CancellationToken ct)
    {
        var rows = await CatalogQueryBuilder
            .Project(CatalogQueryBuilder.ApplyTrigramFallback(
                CatalogQueryBuilder.Filter(source, query), q).Take(limit))
            .ToListAsync(ct);

        // Промах поиска внутри ленты одного продавца — не пробел в каталоге.
        if (rows.Count == 0 && string.IsNullOrEmpty(query.Seller))
        {
            // Ни FTS, ни fuzzy — это пробел в каталоге, логируем для наполнения.
            db.SearchMisses.Add(new SearchMiss { Query = q });
            await db.SaveChangesAsync(ct);
        }

        var items = await HighlightAsync(rows, q, ct);
        return await OkPageAsync(new CatalogPageResponse(items, NextCursor: null, HasMore: false), ct);
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

    /// <summary>
    /// Отдаёт страницу каталога, дозаполнив <c>isFavorite</c> для текущего пользователя
    /// ОДНИМ запросом на всю страницу (список Id + WHERE IN), не по одному на карточку.
    /// Аноним и пустая страница — без дополнительного запроса.
    /// </summary>
    private async Task<ActionResult<CatalogPageResponse>> OkPageAsync(CatalogPageResponse page, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null || page.Items.Count == 0)
            return Ok(page);

        var ids = page.Items.Select(i => i.Id).ToList();
        var favIds = await db.Favorites.AsNoTracking()
            .Where(f => f.UserId == userId.Value && ids.Contains(f.ListingId))
            .Select(f => f.ListingId)
            .ToHashSetAsync(ct);

        if (favIds.Count == 0)
            return Ok(page);

        var items = page.Items
            .Select(i => favIds.Contains(i.Id) ? i with { IsFavorite = true } : i)
            .ToList();
        return Ok(page with { Items = items });
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
            // Неизвестный продавец — 0, а не размер всего каталога.
            total = await CatalogSourceAsync(query, ct) is { } source
                ? await CatalogQueryBuilder.Filter(source, query).LongCountAsync(ct)
                : 0;
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

        return Ok(await ToResponseAsync(listing, await RevealCountAsync(listing.Id, ct), await IsFavoriteAsync(listing.Id, ct)));
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

        return Ok(await ToResponseAsync(listing, await RevealCountAsync(listing.Id, ct), await IsFavoriteAsync(listing.Id, ct)));
    }

    /// <summary>
    /// Раскрытие контактов продавца. Отдаёт телефон и deeplink'и мессенджеров
    /// ТОЛЬКО если объявление Active, продавец не забанен и ShowPhoneInListing = true;
    /// иначе — единый 404 без объяснения причины. Телефон нигде больше в API не отдаётся.
    /// Анти-скрейпинг: rate-limit по (IpHash, UserId), задержка анонимам, журнал раскрытий.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Contact)]
    [HttpGet("{id:guid}/contact")]
    public async Task<ActionResult<SellerContactResponse>> GetContact(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId();
        var ipHash = contactReveal.HashIp(ClientIp());

        // Rate-limit раскрытия — на встроенном RateLimiter (политика "contact"): аноним по IP,
        // авторизованный по пользователю. Здесь остаётся только анти-скрейпинг задержка/журнал.

        // Анонимов намеренно замедляем — массовый обход дороже единичного просмотра.
        if (userId is null)
            await contactReveal.DelayAnonymousAsync(ct);

        // Телефон/username продавца читаются ТОЛЬКО здесь и только для построения ссылок.
        var seller = await db.Listings.AsNoTracking()
            .Where(l => l.Id == id && l.Status == ListingStatus.Active)
            .Select(l => new
            {
                l.Owner!.IsBanned,
                l.Owner.PhoneE164,
                ShowPhone = l.Owner.Profile!.ShowPhoneInListing,
                l.Owner.Profile.TelegramUsername,
                l.Owner.Profile.ViberEnabled,
                l.Owner.Profile.WhatsappEnabled
            })
            .FirstOrDefaultAsync(ct);

        // Единый 404 без деталей: нет объявления / не Active / бан / показ выключен / нет телефона.
        if (seller is null || seller.IsBanned || !seller.ShowPhone || string.IsNullOrEmpty(seller.PhoneE164))
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        await contactReveal.RecordAsync(id, userId, ipHash, ct);

        return Ok(ContactLinkBuilder.Build(
            seller.PhoneE164, seller.TelegramUsername, seller.ViberEnabled, seller.WhatsappEnabled));
    }

    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.CreateListing)]
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
            var (guard, author) = await PublishGuardAsync(userId, ct);
            if (guard is not null)
                return guard;

            // hasImages: true намеренно. Объявление создаётся одним запросом, фотографии
            // грузятся следующим — на этот момент их не может быть физически ни у кого,
            // и штраф «нет фото» ударил бы по всем без разбора. На редактировании и
            // восстановлении из архива признак уже считается по факту.
            var decision = moderation.Resolve(
                author, request.Title, request.Description, request.Price, request.Category, hasImages: true);
            listing.Publish(decision.Mode, DateTimeOffset.UtcNow, decision.Priority);

            // Попало в каталог (Auto или постмодерация) — анонсируем в Telegram-канал
            // в той же транзакции. При премодерации канал ждёт одобрения.
            if (listing.Status == ListingStatus.Active)
                EnqueueChannelPublish(listing.Id);
        }

        await SaveNewWithSlugAsync(listing, ct);
        return CreatedAtAction(nameof(GetById), new { id = listing.Id }, await ToResponseAsync(listing));
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

        // Существенно ли меняется объявление — считаем ДО присваивания новых значений.
        var substantial = IsSubstantialEdit(listing, request);

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

        // Главный обходной путь автопубликации: опубликовать безобидное объявление,
        // дождаться выхода в каталог и переписать текст. Поэтому существенная правка
        // уже опубликованного объявления заново проходит политику модерации — и при
        // низком score уезжает из каталога обратно на премодерацию.
        if (substantial && listing.Status is ListingStatus.Active or ListingStatus.PendingReview)
        {
            var author = await db.Users.FirstAsync(u => u.Id == userId, ct);
            var decision = moderation.Resolve(
                author, listing.Title, listing.Description, listing.Price, listing.Category,
                await HasImagesAsync(listing.Id, ct));

            var wasInCatalog = listing.Status == ListingStatus.Active;
            listing.SendToReview(decision.Mode, DateTimeOffset.UtcNow, decision.Priority);

            // Ушло из каталога на повторную премодерацию — снимаем пост в канале.
            if (wasInCatalog && listing.Status == ListingStatus.PendingReview)
                EnqueueChannelMark(listing.Id, ChannelMark.Archived);
        }

        await db.SaveChangesAsync(ct);
        return Ok(await ToResponseAsync(listing, await RevealCountAsync(listing.Id, ct)));
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

        // Снятие с публикации = уход в архив (переход — доменным методом).
        listing.Archive(DateTimeOffset.UtcNow);
        // Помечаем пост в канале «Снято с публикации» (обработчик стерпит отсутствие поста).
        EnqueueChannelMark(listing.Id, ChannelMark.Archived);
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

        var (guard, author) = await PublishGuardAsync(userId, ct);
        if (guard is not null)
            return guard;

        var decision = moderation.Resolve(
            author, listing.Title, listing.Description, listing.Price, listing.Category,
            await HasImagesAsync(listing.Id, ct));
        listing.Publish(decision.Mode, DateTimeOffset.UtcNow, decision.Priority);

        // Попало в каталог (Auto или постмодерация) — анонсируем в Telegram-канал.
        if (listing.Status == ListingStatus.Active)
            EnqueueChannelPublish(listing.Id);

        await db.SaveChangesAsync(ct);

        return Ok(await ToResponseAsync(listing, await RevealCountAsync(listing.Id, ct)));
    }

    /// <summary>
    /// Поднятие объявления: обновляет BumpedAt (объявление всплывает в начало каталога).
    /// Бесплатно, не чаще одного раза в N дней (платных услуг в MVP нет). Лимит проверяется
    /// на сервере под блокировкой строки (SELECT ... FOR UPDATE): два параллельных запроса
    /// не поднимут объявление дважды — второй дождётся первого и увидит свежий BumpedAt.
    /// </summary>
    [Authorize]
    [HttpPost("{id:guid}/bump")]
    public async Task<ActionResult<ListingResponse>> Bump(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromDays(hygiene.Value.BumpCooldownDays);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // FOR UPDATE держит блокировку строки до конца транзакции. IgnoreQueryFilters —
        // чтобы EF не оборачивал SQL подзапросом (тогда FOR UPDATE был бы не на верхнем уровне);
        // мягкое удаление проверяем вручную ниже.
        var listing = (await db.Listings
            .FromSql($"SELECT * FROM listings WHERE \"Id\" = {id} FOR UPDATE")
            .IgnoreQueryFilters()
            .ToListAsync(ct)).FirstOrDefault();

        if (listing is null || listing.DeletedAt != null || listing.OwnerId != userId)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        if (listing.Status != ListingStatus.Active)
            return Problem(title: "Поднять можно только активное объявление",
                statusCode: StatusCodes.Status409Conflict);

        if (listing.BumpedAt is { } last && now - last < cooldown)
        {
            var retryAfter = last + cooldown - now;
            return TooManyRequests(
                (int)Math.Ceiling(retryAfter.TotalSeconds),
                title: $"Поднимать объявление можно раз в {hygiene.Value.BumpCooldownDays} дн.");
        }

        listing.Bump(now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(await ToResponseAsync(listing, await RevealCountAsync(listing.Id, ct)));
    }

    /// <summary>
    /// Отметить объявление проданным: Active → Sold. Оно остаётся доступным по прямой
    /// ссылке (там отзывы и история), но уходит из каталога и получает noindex на фронте.
    /// Обратный переход Sold → Active разрешён в течение N дней (см. <see cref="Reactivate"/>).
    /// </summary>
    [Authorize]
    [HttpPost("{id:guid}/mark-sold")]
    public async Task<ActionResult<ListingResponse>> MarkSold(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        if (listing.Status != ListingStatus.Active)
            return Problem(title: "Отметить проданным можно только активное объявление",
                statusCode: StatusCodes.Status409Conflict);

        listing.MarkSold(DateTimeOffset.UtcNow);
        // Помечаем пост в канале «Продано» (в той же транзакции; обработчик стерпит отсутствие поста).
        EnqueueChannelMark(listing.Id, ChannelMark.Sold);
        await db.SaveChangesAsync(ct);

        return Ok(await ToResponseAsync(listing, await RevealCountAsync(listing.Id, ct)));
    }

    /// <summary>
    /// Вернуть проданное объявление в продажу: Sold → Active. Разрешено только в течение
    /// <see cref="CatalogHygieneOptions.SoldReactivationDays"/> дней с момента продажи.
    /// </summary>
    [Authorize]
    [HttpPost("{id:guid}/reactivate")]
    public async Task<ActionResult<ListingResponse>> Reactivate(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        if (listing.Status != ListingStatus.Sold)
            return Problem(title: "Вернуть в продажу можно только проданное объявление",
                statusCode: StatusCodes.Status409Conflict);

        var window = TimeSpan.FromDays(hygiene.Value.SoldReactivationDays);
        if (listing.SoldAt is { } soldAt && DateTimeOffset.UtcNow - soldAt > window)
            return Problem(
                title: $"Вернуть в продажу можно только в течение {hygiene.Value.SoldReactivationDays} дн. после продажи",
                statusCode: StatusCodes.Status409Conflict);

        listing.ReactivateFromSold(DateTimeOffset.UtcNow);
        // Вернулось в продажу — снимаем пометку «Продано» с поста (обработчик правит подпись).
        EnqueueChannelPublish(listing.Id);
        await db.SaveChangesAsync(ct);

        return Ok(await ToResponseAsync(listing, await RevealCountAsync(listing.Id, ct)));
    }

    /// <summary>
    /// Восстановить объявление из архива: Archived → Active. Разрешено, если с момента
    /// архивации прошло не более <see cref="CatalogHygieneOptions.RestoreWithinDays"/> дней.
    /// Если автор получал reject за последние N дней — восстановление проходит премодерацию
    /// заново (Archived → PendingReview).
    /// </summary>
    [Authorize]
    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult<ListingResponse>> Restore(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id && l.OwnerId == userId, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        if (listing.Status != ListingStatus.Archived)
            return Problem(title: "Восстановить можно только объявление из архива",
                statusCode: StatusCodes.Status409Conflict);

        var window = TimeSpan.FromDays(hygiene.Value.RestoreWithinDays);
        if (listing.ArchivedAt is { } archivedAt && DateTimeOffset.UtcNow - archivedAt > window)
            return Problem(
                title: $"Восстановить можно только в течение {hygiene.Value.RestoreWithinDays} дн. после архивации",
                statusCode: StatusCodes.Status409Conflict);

        // Восстановление — это возвращение в каталог, то есть публикация: те же ворота,
        // что у POST /publish (подтверждённый контакт, лимит «в обороте»). Без этой
        // проверки restore был бы обходом обоих ограничений.
        var (guard, author) = await PublishGuardAsync(userId, ct);
        if (guard is not null)
            return guard;

        // И та же политика модерации: свежий отказ модератора или рисковый текст
        // (объявление могли отредактировать, пока оно лежало в архиве) снова уводят
        // объявление на проверку.
        var decision = moderation.Resolve(
            author, listing.Title, listing.Description, listing.Price, listing.Category,
            await HasImagesAsync(listing.Id, ct));
        listing.RestoreFromArchive(decision.Mode, DateTimeOffset.UtcNow, decision.Priority);

        // Вернулось в каталог (Active) — снимаем пометку «Снято» с поста. При уходе на
        // повторную премодерацию (PendingReview) канал не трогаем: снова опубликуем при одобрении.
        if (listing.Status == ListingStatus.Active)
            EnqueueChannelPublish(listing.Id);

        await db.SaveChangesAsync(ct);

        return Ok(await ToResponseAsync(listing, await RevealCountAsync(listing.Id, ct)));
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

        var listings = await query
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);

        // Агрегат раскрытий — одним GROUP BY по всем id, без N+1.
        var ids = listings.Select(l => l.Id).ToList();
        var counts = await db.ContactReveals
            .Where(r => ids.Contains(r.ListingId))
            .GroupBy(r => r.ListingId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // Владелец у всех строк один — код берётся из кеша ToResponseAsync, не запросом на строку.
        var items = new List<ListingResponse>(listings.Count);
        foreach (var l in listings)
            items.Add(await ToResponseAsync(l, counts.GetValueOrDefault(l.Id), ct: ct));

        return Ok(items);
    }

    /// <summary>
    /// Счётчики «моих объявлений» по статусам — для вкладок над списком.
    /// Ответ: <c>{ "all": 12, "Draft": 3, "PendingReview": 0, "Active": 7, ... }</c>.
    ///
    /// Ключи статусов — те же литералы, что принимает <c>?status=</c> у
    /// <see cref="MyListings"/>, поэтому вкладка подставляет ключ в фильтр как есть.
    /// Статусы с нулём тоже присутствуют: дорисовывать недостающие — не работа клиента.
    ///
    /// Условия выборки обязаны совпадать со списком. Здесь это тот же
    /// <c>db.Listings</c> без <c>IgnoreQueryFilters()</c>, поэтому глобальный фильтр
    /// мягкого удаления (<c>DeletedAt == null</c>) применяется к счётчикам так же,
    /// как и к списку — иначе сумма разошлась бы с выдачей на число удалённых.
    /// </summary>
    [Authorize]
    [HttpGet("~/api/me/listings/counts")]
    public async Task<ActionResult<IReadOnlyDictionary<string, int>>> MyListingCounts(
        CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        // Один запрос: GROUP BY по статусу. Покрывается IX_listings_OwnerId_Status.
        var groups = await db.Listings
            .AsNoTracking()
            .Where(l => l.OwnerId == userId)
            .GroupBy(l => l.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // "all" — сумма уже полученных групп, а не второй COUNT(*): отдельный
        // запрос мог бы увидеть другое состояние таблицы и разойтись с частями.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["all"] = groups.Sum(g => g.Count)
        };

        foreach (var status in Enum.GetValues<ListingStatus>())
            counts[status.ToString()] = 0;

        foreach (var group in groups)
            counts[group.Status.ToString()] = group.Count;

        return Ok(counts);
    }

    // ---- helpers ----

    /// <summary>Общие проверки фильтров каталога (для /listings и /listings/count). null — ок.</summary>
    private ObjectResult? ValidateFilters(CatalogQuery query)
    {
        if (CatalogQueryBuilder.ValidateFilters(query.Cities, query.PriceFrom, query.PriceTo) is { } error)
            return Problem(title: error, statusCode: StatusCodes.Status400BadRequest);

        return null;
    }

    /// <summary>
    /// Источник выборки каталога с учётом продавца. Без <c>seller</c> — все объявления
    /// (фильтр Active и остальные накладывает <see cref="CatalogQueryBuilder.Filter"/>).
    /// С <c>seller</c> — только объявления владельца этого «ID профиля».
    /// <para>
    /// null — код не той формы, не существует или аккаунт удалён: вызывающий отдаёт
    /// пустую выдачу. Раньше неизвестный параметр молча игнорировался, и профиль
    /// продавца показывал ВЕСЬ каталог. Ответ для несуществующего кода совпадает
    /// с ответом для продавца без активных объявлений — перебор кодов через каталог
    /// выдаёт лишь тех, чьи объявления и так видны в общей витрине.
    /// </para>
    /// </summary>
    private async Task<IQueryable<Listing>?> CatalogSourceAsync(CatalogQuery query, CancellationToken ct)
    {
        var source = db.Listings.AsNoTracking();
        if (string.IsNullOrEmpty(query.Seller))
            return source;

        return await publicCodes.ResolveAsync(query.Seller, ct) is { } ownerId
            ? source.Where(l => l.OwnerId == ownerId)
            : null;
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
            q.PriceType?.ToString() ?? "-",
            string.IsNullOrEmpty(q.Seller) ? "-" : q.Seller);
    }

    /// <summary>
    /// Проверки при публикации: подтверждённый контакт + лимит «в обороте».
    /// Error = null — публиковать можно; заодно отдаёт загруженного автора, чтобы
    /// политика модерации считала доверие по его денормализованным полям и не ходила
    /// в БД второй раз.
    /// </summary>
    private async Task<(ObjectResult? Error, User Author)> PublishGuardAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        var (canPublish, reason) = publishing.CanPublish(user);
        if (!canPublish)
            return (Problem(title: reason, statusCode: StatusCodes.Status403Forbidden), user);

        var inCirculation = await db.Listings
            .CountAsync(l => l.OwnerId == userId && InCirculation.Contains(l.Status), ct);
        if (inCirculation >= options.Value.MaxActivePerUser)
            return (Problem(title: "Достигнут лимит активных объявлений, архивируйте лишние",
                statusCode: StatusCodes.Status409Conflict), user);

        return (null, user);
    }

    /// <summary>Есть ли у объявления хотя бы одна фотография (вход риск-оценки).</summary>
    private Task<bool> HasImagesAsync(Guid listingId, CancellationToken ct) =>
        db.ListingImages.AnyAsync(i => i.ListingId == listingId, ct);

    /// <summary>
    /// Существенная ли правка — то есть меняет ли она то, что оценивала модерация:
    /// текст, цену и категорию. Город, район и состояние на решение не влияют,
    /// и гонять объявление по очереди из-за смены района незачем.
    /// </summary>
    private static bool IsSubstantialEdit(Listing listing, UpdateListingRequest request) =>
        !string.Equals(listing.Title, request.Title, StringComparison.Ordinal) ||
        !string.Equals(listing.Description, request.Description, StringComparison.Ordinal) ||
        listing.Price != request.Price ||
        listing.PriceType != request.PriceType ||
        listing.Category != request.Category ||
        listing.SubcategoryId != request.SubcategoryId;

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

    /// <summary>
    /// Ставит в outbox пост объявления в Telegram-канал (перешло в Active). Обработчик
    /// идемпотентен: повторную активацию он превращает в правку подписи, а не в новый пост.
    /// Пишется в той же транзакции, что и доменное изменение (общий SaveChanges).
    /// </summary>
    private void EnqueueChannelPublish(Guid listingId) =>
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.ListingPublished,
            Payload = JsonSerializer.Serialize(new { listingId })
        });

    /// <summary>Ставит в outbox правку поста в канале: пометка «Продано»/«Снято с публикации».</summary>
    private void EnqueueChannelMark(Guid listingId, string mark) =>
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.ListingChannelUpdate,
            Payload = JsonSerializer.Serialize(new { listingId, mark })
        });

    private ActionResult Invalid(ValidationResult validation)
    {
        foreach (var error in validation.Errors)
            ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
        return ValidationProblem(ModelState);
    }

    /// <summary>Мапит сущность в DTO, досчитывая daysUntilArchive и канонический URL по конфигурации.</summary>
    private async Task<ListingResponse> ToResponseAsync(
        Listing l, int contactRevealCount = 0, bool isFavorite = false, CancellationToken ct = default) =>
        Map(l, await OwnerCodeAsync(l.OwnerId, ct),
            contactRevealCount, isFavorite, DaysUntilArchive(l), CanonicalUrl(l.Slug),
            // Причина отклонения — только владельцу. DTO один и для публичной
            // карточки, и для «моих объявлений», поэтому условие здесь явное.
            isOwner: CurrentUserId() == l.OwnerId);

    private static ListingResponse Map(
        Listing l, string ownerPublicCode,
        int contactRevealCount, bool isFavorite, int? daysUntilArchive, string? canonicalUrl,
        bool isOwner) => new(
        l.Id, l.Slug, l.Title, l.Description, l.Price, l.PriceType, l.Category,
        l.SubcategoryId, l.City, l.District, l.Condition, l.Status,
        l.ViewsCount, ownerPublicCode, l.CreatedAt, l.PublishedAt, contactRevealCount,
        l.FavoritesCount, isFavorite, daysUntilArchive,
        // Дальше — только именованные аргументы: хвост DTO состоит из
        // необязательных параметров, и позиционная передача молча уехала бы
        // в соседнее поле того же типа (string? рядом с string?).
        RejectionReasonCode: isOwner ? l.RejectionReasonCode?.ToString() : null,
        RejectionComment: isOwner ? l.RejectionComment : null,
        RejectedAt: isOwner ? l.RejectedAt : null,
        CanonicalUrl: canonicalUrl);

    /// <summary>
    /// «ID профиля» владельца по его Guid. Кеш — на время запроса (контроллер scoped):
    /// в списке своих объявлений владелец один, и без кеша это был бы запрос на строку.
    /// </summary>
    private readonly Dictionary<Guid, string> _ownerCodes = [];

    private async Task<string> OwnerCodeAsync(Guid ownerId, CancellationToken ct)
    {
        if (_ownerCodes.TryGetValue(ownerId, out var cached))
            return cached;

        var code = await db.Users.AsNoTracking()
            .Where(u => u.Id == ownerId)
            .Select(u => u.PublicCode)
            .FirstAsync(ct);

        _ownerCodes[ownerId] = code;
        return code;
    }

    /// <summary>Канонический адрес карточки. null, если публичный адрес сайта не настроен.</summary>
    private string? CanonicalUrl(string slug) =>
        seo.Value.NormalizedBaseUrl is { } baseUrl ? SeoUrls.Listing(baseUrl, slug) : null;

    /// <summary>Сколько дней до автоархивации. null для не-Active (у них срок не идёт).</summary>
    private int? DaysUntilArchive(Listing l)
    {
        if (l.Status != ListingStatus.Active)
            return null;
        var basis = l.BumpedAt ?? l.PublishedAt ?? l.CreatedAt;
        var archiveAt = basis.AddDays(hygiene.Value.ArchiveAfterDays);
        var remaining = (archiveAt - DateTimeOffset.UtcNow).TotalDays;
        return (int)Math.Max(0, Math.Ceiling(remaining));
    }

    // Факт свежего отказа больше не собирается джойном по журналу модерации: он
    // денормализован в users.LastRejectedAt (триггер listings_trust_sync) и читается
    // политикой из уже загруженной строки автора.

    /// <summary>Число раскрытий контактов по объявлению — отдельным запросом (не N+1).</summary>
    private Task<int> RevealCountAsync(Guid listingId, CancellationToken ct) =>
        db.ContactReveals.CountAsync(r => r.ListingId == listingId, ct);

    /// <summary>В избранном ли объявление у текущего пользователя (false для анонима).</summary>
    private async Task<bool> IsFavoriteAsync(Guid listingId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        return userId is not null && await db.Favorites.AsNoTracking()
            .AnyAsync(f => f.UserId == userId.Value && f.ListingId == listingId, ct);
    }
}
