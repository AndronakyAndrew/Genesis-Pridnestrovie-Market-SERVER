using System.Globalization;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Избранное: добавление/удаление объявления и список «моего избранного».
/// Идемпотентность — на уровне БД (составной PK favorites), счётчик
/// <c>FavoritesCount</c> объявления поддерживается триггером БД.
/// </summary>
[Authorize]
public class FavoritesController(AppDbContext db) : ApiControllerBase
{
    /// <summary>Лимит записей избранного на пользователя.</summary>
    private const int MaxPerUser = 500;

    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    /// <summary>Маркер сортировки курсора «моего избранного» (свежие сверху).</summary>
    private const string CursorToken = "fav";

    /// <summary>
    /// Добавить объявление в избранное. Идемпотентно: повторный вызов возвращает 200
    /// и НЕ создаёт дубль (составной PK). Нельзя добавить собственное объявление (400).
    /// </summary>
    [HttpPost("~/api/listings/{id:guid}/favorite")]
    public async Task<ActionResult<FavoriteResponse>> Add(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        // Читаем только владельца — существование проверяется тем же запросом.
        // Soft-deleted объявление отсекается глобальным фильтром ⇒ 404.
        var owner = await db.Listings.AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => (Guid?)l.OwnerId)
            .FirstOrDefaultAsync(ct);

        if (owner is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        if (owner.Value == userId)
            return Problem(title: "Нельзя добавить в избранное собственное объявление",
                statusCode: StatusCodes.Status400BadRequest);

        // Уже в избранном — идемпотентный успех без вставки и без изменения счётчика.
        if (await db.Favorites.AnyAsync(f => f.UserId == userId && f.ListingId == id, ct))
            return Ok(new FavoriteResponse(IsFavorite: true, await FavoritesCountAsync(id, ct)));

        var total = await db.Favorites.CountAsync(f => f.UserId == userId, ct);
        if (total >= MaxPerUser)
            return Problem(title: $"Достигнут лимит избранного ({MaxPerUser}), удалите лишнее",
                statusCode: StatusCodes.Status409Conflict);

        db.Favorites.Add(new Favorite { UserId = userId, ListingId = id });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Конкурентная вставка того же (UserId, ListingId) — тоже идемпотентный успех.
        }

        return Ok(new FavoriteResponse(IsFavorite: true, await FavoritesCountAsync(id, ct)));
    }

    /// <summary>
    /// Убрать объявление из избранного. Идемпотентно: если записи нет — тоже 204.
    /// Счётчик <c>FavoritesCount</c> уменьшает триггер БД (только при реальном удалении).
    /// </summary>
    [HttpDelete("~/api/listings/{id:guid}/favorite")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        await db.Favorites
            .Where(f => f.UserId == userId && f.ListingId == id)
            .ExecuteDeleteAsync(ct);

        return NoContent();
    }

    /// <summary>
    /// «Моё избранное»: курсорная пагинация, те же DTO, что в каталоге.
    /// Свежие сверху (по времени добавления). Архивированное/удалённое объявление
    /// остаётся в списке с флагом <c>isUnavailable</c>, а не исчезает молча.
    /// </summary>
    [HttpGet("~/api/me/favorites")]
    public async Task<ActionResult<CatalogPageResponse>> MyFavorites(
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        // IgnoreQueryFilters: soft-deleted объявление НЕ выпадает из избранного молча,
        // а показывается недоступным (isUnavailable). Фильтруем по владельцу избранного.
        var favorites = db.Favorites.AsNoTracking().IgnoreQueryFilters()
            .Where(f => f.UserId == userId);

        if (cursor is { Length: > 0 })
        {
            if (!CatalogCursor.TryDecode(cursor, CursorToken, out var key, out var afterId)
                || !long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);

            var after = new DateTimeOffset(ticks, TimeSpan.Zero);
            favorites = favorites.Where(f =>
                f.CreatedAt < after ||
                (f.CreatedAt == after && f.ListingId.CompareTo(afterId) < 0));
        }

        var rows = await favorites
            .OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.ListingId)
            .Take(take + 1)
            .Select(f => new FavoriteRow(
                f.ListingId,
                f.CreatedAt,
                f.Listing!.Slug,
                f.Listing.Title,
                f.Listing.Price,
                f.Listing.PriceType,
                f.Listing.City,
                f.Listing.Category,
                f.Listing.Images.OrderBy(i => i.SortOrder).Select(i => i.ThumbKey).FirstOrDefault(),
                f.Listing.PublishedAt,
                f.Listing.FavoritesCount,
                f.Listing.Status != ListingStatus.Active || f.Listing.DeletedAt != null))
            .ToListAsync(ct);

        var hasMore = rows.Count > take;
        var page = rows.Take(take).ToList();

        var items = page.Select(r => new ListingCardResponse(
            r.Id, r.Slug, r.Title, r.Price, r.PriceType, r.City, r.Category,
            r.FirstImageUrl, r.PublishedAt, IsBumped: false,
            FavoritesCount: r.FavoritesCount, IsFavorite: true, IsUnavailable: r.IsUnavailable)).ToList();

        var nextCursor = hasMore
            ? CatalogCursor.Encode(CursorToken,
                page[^1].FavoritedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), page[^1].Id)
            : null;

        return Ok(new CatalogPageResponse(items, nextCursor, hasMore));
    }

    private Task<int> FavoritesCountAsync(Guid listingId, CancellationToken ct) =>
        db.Listings.IgnoreQueryFilters()
            .Where(l => l.Id == listingId)
            .Select(l => l.FavoritesCount)
            .FirstAsync(ct);

    /// <summary>Проекция строки избранного: поля карточки + время добавления для курсора.</summary>
    private sealed record FavoriteRow(
        Guid Id,
        DateTimeOffset FavoritedAt,
        string Slug,
        string Title,
        decimal? Price,
        PriceType PriceType,
        City City,
        Category Category,
        string? FirstImageUrl,
        DateTimeOffset? PublishedAt,
        int FavoritesCount,
        bool IsUnavailable);
}
