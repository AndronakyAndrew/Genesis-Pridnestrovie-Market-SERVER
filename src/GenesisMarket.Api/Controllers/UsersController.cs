using System.Linq.Expressions;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Http;
using GenesisMarket.Api.Profiles;
using GenesisMarket.Api.Security;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Публичные профили продавцов. Ни email, ни телефона, ни точной даты
/// регистрации, ни Role, ни IsBanned наружу не отдаём.
///
/// Основной публичный адрес — по «ID профиля» (<c>by-code/82914</c>).
/// Маршруты по Guid сохранены как legacy: по ним уже расшарены ссылки, ломать
/// их нельзя, но новые адреса мы больше нигде не выдаём. Guid — это UUID v7,
/// в первых 48 битах которого лежит время регистрации с точностью до
/// миллисекунды; публичный профиль при этом намеренно округляет дату
/// регистрации до месяца, так что адрес по Guid обесценивал это округление.
/// </summary>
[Route("api/users")]
public class UsersController(
    AppDbContext db,
    IObjectStorage storage,
    IPublicCodeResolver publicCodes) : ApiControllerBase
{
    /// <summary>Профиль по «ID профиля» — основной публичный адрес.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.ProfileByCode)]
    [HttpGet("by-code/{code}/public")]
    public async Task<ActionResult<PublicProfileResponse>> GetPublicByCode(string code, CancellationToken ct)
    {
        var id = await publicCodes.ResolveAsync(code, ct);
        return id is null ? ProfileNotFound() : await PublicProfileAsync(id.Value, ct);
    }

    /// <summary>Профиль по Guid — legacy-адрес, поддерживается ради старых ссылок.</summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}/public")]
    public Task<ActionResult<PublicProfileResponse>> GetPublic(Guid id, CancellationToken ct) =>
        PublicProfileAsync(id, ct);

    /// <summary>
    /// Публичная выдача аватара через API. MinIO находится в приватной Docker-сети,
    /// поэтому его внутренний адрес нельзя отдавать браузеру напрямую.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.ProfileByCode)]
    [HttpGet("by-code/{code}/avatar")]
    public Task<IActionResult> GetAvatarByCode(string code, CancellationToken ct) =>
        AvatarAsync(p => p.User!.PublicCode == code, ct);

    /// <summary>Аватар по Guid — legacy-адрес (см. комментарий к контроллеру).</summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}/avatar")]
    public Task<IActionResult> GetAvatar(Guid id, CancellationToken ct) =>
        AvatarAsync(p => p.UserId == id, ct);

    // ---- общая часть обоих адресов ----

    private async Task<ActionResult<PublicProfileResponse>> PublicProfileAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users
            .AsNoTracking()
            .Include(u => u.Profile)
            .Include(u => u.BusinessProfile)
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null || user.IsDeleted || user.Profile is null)
            return ProfileNotFound();

        var activeListings = await db.Listings
            .CountAsync(l => l.OwnerId == id && l.Status == ListingStatus.Active, ct);

        // Дата регистрации — только месяц и год (первое число месяца).
        var registeredAt = new DateOnly(user.CreatedAt.Year, user.CreatedAt.Month, 1);

        // Реквизиты — только подтверждённого бизнеса. Вернувшийся в Private или
        // не прошедший проверку ничего из них публично не показывает.
        var business = user.BusinessProfile is { IsVerifiedBusiness: true } b
            ? new PublicBusinessInfo(b.ShopName, b.LegalForm, b.RegistrationNumber, b.PickupAddress)
            : null;

        return Ok(new PublicProfileResponse(
            // «ID профиля» — то, чем продавца называют в переписке и в поддержке.
            user.PublicCode,
            user.Profile.DisplayName,
            user.Profile.City,
            user.Profile.AvatarUrl is null
                ? null
                : AvatarUrl.Build(Request, user.PublicCode, user.Profile.UpdatedAt),
            // «О себе» — публично по замыслу: продавец пишет этот текст покупателям.
            user.Profile.Description,
            registeredAt,
            activeListings,
            // Денормализованный агрегат отзывов (поддерживается триггером reviews_rating_sync).
            AverageRating: user.AverageRating,
            ReviewsCount: user.ReviewsCount,
            user.PhoneVerified,
            IsVerifiedBusiness: business is not null,
            Business: business,
            EmailVerified: user.EmailVerified));
    }

    private async Task<IActionResult> AvatarAsync(Expression<Func<Profile, bool>> match, CancellationToken ct)
    {
        var avatar = await db.Profiles.AsNoTracking()
            .Where(match)
            .Where(p => !p.User!.IsDeleted && p.AvatarUrl != null)
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

    private ObjectResult ProfileNotFound() =>
        Problem(title: "Профиль не найден", statusCode: StatusCodes.Status404NotFound);

    private static string ContentTypeFor(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };
}
