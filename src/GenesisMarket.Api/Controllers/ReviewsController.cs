using System.Globalization;
using System.Text.Json;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Отзывы о продавцах — ядро слоя доверия. Оставить отзыв можно только после
/// реального раскрытия контактов по объявлению; один отзыв на пару (автор,
/// объявление); нельзя оценить сам себя. Агрегат рейтинга у продавца
/// (<c>AverageRating</c>/<c>ReviewsCount</c>) пересчитывается триггером БД
/// в той же транзакции, что и запись/редактирование/скрытие отзыва.
/// </summary>
[Route("api/reviews")]
public class ReviewsController(AppDbContext db) : ApiControllerBase
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    /// <summary>Маркер сортировки курсора отзывов (свежие сверху).</summary>
    private const string CursorToken = "rev";

    /// <summary>Окно, в течение которого автор может редактировать свой отзыв.</summary>
    private static readonly TimeSpan EditWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Оставить отзыв о продавце по объявлению. Требует, чтобы автор ранее
    /// раскрывал контакты по этому объявлению (анти-накрутка). Дубликат по паре
    /// (автор, объявление) — 409. Отзыв самому себе — 400.
    /// </summary>
    [Authorize]
    [HttpPost]
    public async Task<ActionResult<ReviewResponse>> Create(CreateReviewRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var listing = await db.Listings.AsNoTracking()
            .Where(l => l.Id == request.ListingId)
            .Select(l => new { l.OwnerId })
            .FirstOrDefaultAsync(ct);

        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        if (listing.OwnerId == userId)
            return Problem(title: "Нельзя оставить отзыв на собственное объявление",
                statusCode: StatusCodes.Status400BadRequest);

        // Гейт анти-накрутки: отзыв возможен только после реального раскрытия
        // контактов ЭТИМ пользователем по ЭТОМУ объявлению (журнал ContactReveals).
        var contacted = await db.ContactReveals
            .AnyAsync(r => r.ListingId == request.ListingId && r.ViewerUserId == userId, ct);
        if (!contacted)
            return Problem(
                title: "Отзыв можно оставить только после запроса контактов продавца по этому объявлению",
                statusCode: StatusCodes.Status403Forbidden);

        var text = request.Text.Trim();
        if (text.Length == 0)
            return Problem(title: "Текст отзыва не может быть пустым",
                statusCode: StatusCodes.Status400BadRequest);

        var review = new Review
        {
            ListingId = request.ListingId,
            AuthorId = userId,
            TargetUserId = listing.OwnerId,
            Rating = request.Rating,
            Text = text
        };
        db.Reviews.Add(review);

        // Уведомление адресату отзыва — через outbox в той же транзакции (SaveChanges).
        // Только id отзыва: адресата, оценку и текст обработчик достанет из БД.
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.NewReview,
            Payload = JsonSerializer.Serialize(new { reviewId = review.Id })
        });

        try
        {
            // Триггер reviews_rating_sync пересчитывает агрегат продавца в этой же транзакции.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return Problem(title: "Вы уже оставили отзыв по этому объявлению",
                statusCode: StatusCodes.Status409Conflict);
        }

        var author = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.DisplayName, p.AvatarUrl })
            .FirstAsync(ct);

        var response = new ReviewResponse(
            review.Id, review.ListingId, review.AuthorId,
            author.DisplayName, author.AvatarUrl,
            review.Rating, review.Text, review.CreatedAt, review.UpdatedAt,
            IsEditable: true);

        return Created((string?)null, response);
    }

    /// <summary>
    /// Отзывы о продавце: курсорная пагинация, свежие сверху, скрытые модератором
    /// не отдаются. Публичный эндпоинт.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("~/api/users/{id:guid}/reviews")]
    public async Task<ActionResult<ReviewsPageResponse>> ForUser(
        Guid id, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var viewer = CurrentUserId();

        var query = db.Reviews.AsNoTracking()
            .Where(r => r.TargetUserId == id && !r.IsHidden);

        if (cursor is { Length: > 0 })
        {
            if (!CatalogCursor.TryDecode(cursor, CursorToken, out var key, out var afterId)
                || !long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);

            var after = new DateTimeOffset(ticks, TimeSpan.Zero);
            query = query.Where(r =>
                r.CreatedAt < after ||
                (r.CreatedAt == after && r.Id.CompareTo(afterId) < 0));
        }

        var rows = await query
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Take(take + 1)
            .Select(r => new ReviewRow(
                r.Id, r.ListingId, r.AuthorId,
                r.Author!.Profile!.DisplayName,
                r.Author.Profile.AvatarUrl,
                r.Rating, r.Text, r.CreatedAt, r.UpdatedAt))
            .ToListAsync(ct);

        var hasMore = rows.Count > take;
        var page = rows.Take(take).ToList();
        var now = DateTimeOffset.UtcNow;

        var items = page.Select(r => new ReviewResponse(
            r.Id, r.ListingId, r.AuthorId, r.AuthorName, r.AuthorAvatarUrl,
            r.Rating, r.Text, r.CreatedAt, r.UpdatedAt,
            IsEditable: viewer == r.AuthorId && now - r.CreatedAt <= EditWindow)).ToList();

        var nextCursor = hasMore
            ? CatalogCursor.Encode(CursorToken,
                page[^1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), page[^1].Id)
            : null;

        return Ok(new ReviewsPageResponse(items, nextCursor, hasMore));
    }

    /// <summary>
    /// Редактирование своего отзыва — только в течение 24 часов после публикации.
    /// Позже отзыв можно лишь скрыть модератором.
    /// </summary>
    [Authorize]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ReviewResponse>> Update(
        Guid id, UpdateReviewRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var review = await db.Reviews.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (review is null || review.IsHidden)
            return Problem(title: "Отзыв не найден", statusCode: StatusCodes.Status404NotFound);

        if (review.AuthorId != userId)
            return Forbid();

        if (DateTimeOffset.UtcNow - review.CreatedAt > EditWindow)
            return Problem(title: "Редактировать отзыв можно только в течение 24 часов после публикации",
                statusCode: StatusCodes.Status409Conflict);

        var text = request.Text.Trim();
        if (text.Length == 0)
            return Problem(title: "Текст отзыва не может быть пустым",
                statusCode: StatusCodes.Status400BadRequest);

        review.Rating = request.Rating;
        review.Text = text;
        review.UpdatedAt = DateTimeOffset.UtcNow;

        // Триггер пересчитывает агрегат (изменение оценки влияет на среднее).
        await db.SaveChangesAsync(ct);

        var author = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.DisplayName, p.AvatarUrl })
            .FirstAsync(ct);

        return Ok(new ReviewResponse(
            review.Id, review.ListingId, review.AuthorId,
            author.DisplayName, author.AvatarUrl,
            review.Rating, review.Text, review.CreatedAt, review.UpdatedAt,
            IsEditable: true));
    }

    /// <summary>
    /// Скрыть отзыв (модерация). Идемпотентно. Скрытый отзыв уходит из публичной
    /// выдачи и из агрегата рейтинга (пересчёт триггером).
    /// </summary>
    [Authorize(Policy = "Moderator")]
    [HttpPost("{id:guid}/hide")]
    public async Task<IActionResult> Hide(Guid id, CancellationToken ct)
    {
        var moderatorId = CurrentUserId()!.Value;

        var review = await db.Reviews.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (review is null)
            return Problem(title: "Отзыв не найден", statusCode: StatusCodes.Status404NotFound);

        if (review.IsHidden)
            return NoContent();

        review.IsHidden = true;
        review.HiddenByUserId = moderatorId;
        review.UpdatedAt = DateTimeOffset.UtcNow;

        // Триггер уберёт отзыв из AverageRating/ReviewsCount продавца.
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>Проекция строки отзыва для выдачи (имя автора из профиля, не email).</summary>
    private sealed record ReviewRow(
        Guid Id,
        Guid ListingId,
        Guid AuthorId,
        string AuthorName,
        string? AuthorAvatarUrl,
        int Rating,
        string Text,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt);
}
