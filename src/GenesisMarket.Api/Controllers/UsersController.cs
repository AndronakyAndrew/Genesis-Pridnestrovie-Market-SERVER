using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Http;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Публичные профили продавцов. Ни email, ни телефона, ни точной даты
/// регистрации, ни Role, ни IsBanned наружу не отдаём.
/// </summary>
[Route("api/users")]
public class UsersController(AppDbContext db, IObjectStorage storage) : ApiControllerBase
{
    [AllowAnonymous]
    [HttpGet("{id:guid}/public")]
    public async Task<ActionResult<PublicProfileResponse>> GetPublic(Guid id, CancellationToken ct)
    {
        var user = await db.Users
            .AsNoTracking()
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null || user.IsDeleted || user.Profile is null)
            return Problem(title: "Профиль не найден", statusCode: StatusCodes.Status404NotFound);

        var activeListings = await db.Listings
            .CountAsync(l => l.OwnerId == id && l.Status == ListingStatus.Active, ct);

        // Дата регистрации — только месяц и год (первое число месяца).
        var registeredAt = new DateOnly(user.CreatedAt.Year, user.CreatedAt.Month, 1);

        return Ok(new PublicProfileResponse(
            user.Profile.DisplayName,
            user.Profile.City,
            user.Profile.AvatarUrl is null ? null : BuildAvatarUrl(user.Id, user.Profile.UpdatedAt),
            registeredAt,
            activeListings,
            // Денормализованный агрегат отзывов (поддерживается триггером reviews_rating_sync).
            AverageRating: user.AverageRating,
            ReviewsCount: user.ReviewsCount,
            user.PhoneVerified));
    }

    /// <summary>
    /// Публичная выдача аватара через API. MinIO находится в приватной Docker-сети,
    /// поэтому его внутренний адрес нельзя отдавать браузеру напрямую.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}/avatar")]
    public async Task<IActionResult> GetAvatar(Guid id, CancellationToken ct)
    {
        var avatar = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == id && !p.User!.IsDeleted && p.AvatarUrl != null)
            .Select(p => new { Key = p.AvatarUrl!, p.UpdatedAt })
            .FirstOrDefaultAsync(ct);

        if (avatar is null)
            return Problem(title: "Аватар не найден", statusCode: StatusCodes.Status404NotFound);

        // ETag считается от ключа в хранилище: он меняется при каждой загрузке нового
        // аватара, поэтому валидатор честный даже при том, что сам URL перезаписываемый.
        // Cache-Control остаётся коротким и без immutable — см. комментарий к ручке.
        var etag = ImageCaching.ETagFor(avatar.Key);
        if (ImageCaching.IsNotModified(Request, etag))
        {
            Response.Headers.CacheControl = "public, max-age=3600";
            Response.Headers.ETag = etag.ToString();
            return StatusCode(StatusCodes.Status304NotModified);
        }

        try
        {
            var stream = await storage.GetAsync(avatar.Key, ct);
            Response.Headers.CacheControl = "public, max-age=3600";
            return File(stream, ContentTypeFor(avatar.Key), lastModified: null, entityTag: etag,
                enableRangeProcessing: true);
        }
        catch (FileNotFoundException)
        {
            return Problem(title: "Аватар не найден", statusCode: StatusCodes.Status404NotFound);
        }
    }

    private string BuildAvatarUrl(Guid userId, DateTimeOffset? updatedAt) =>
        $"{Request.Scheme}://{Request.Host}/api/users/{userId}/avatar?v={updatedAt?.UtcTicks ?? 0}";

    private static string ContentTypeFor(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };
}
