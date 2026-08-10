using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Приватный профиль владельца. Наружу — только DTO; редактирование —
/// через отдельный DTO с фиксированным набором полей (защита от mass assignment).
/// </summary>
[Authorize]
[Route("api/me")]
public class MeController(
    AppDbContext db,
    IObjectStorage storage,
    IRefreshTokenService refreshTokens,
    SecurityStampValidator securityStamp,
    IOptions<PhoneOptions> phoneOptions) : ApiControllerBase
{
    private const long MaxAvatarBytes = 5 * 1024 * 1024;

    [HttpGet]
    public async Task<ActionResult<MeResponse>> GetMe(CancellationToken ct)
    {
        var user = await LoadAsync(ct);
        return user is null
            ? Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound)
            : Ok(MapMe(user));
    }

    [HttpPatch]
    public async Task<ActionResult<MeResponse>> UpdateMe(UpdateMeRequest request, CancellationToken ct)
    {
        var user = await LoadAsync(ct);
        if (user?.Profile is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        var p = user.Profile;

        // Обновляем только переданные поля. Role/Email/IsBanned/… сюда не биндятся.
        if (request.DisplayName is not null) p.DisplayName = request.DisplayName;
        if (request.City is { } city) p.City = city;
        if (request.TelegramUsername is not null) p.TelegramUsername = request.TelegramUsername;
        if (request.ViberEnabled is { } viber) p.ViberEnabled = viber;
        if (request.WhatsappEnabled is { } whatsapp) p.WhatsappEnabled = whatsapp;
        if (request.ShowPhoneInListing is { } showPhone) p.ShowPhoneInListing = showPhone;

        if (request.PhoneE164 is not null)
        {
            var normalized = PhoneNumber.Normalize(request.PhoneE164, phoneOptions.Value.AllowOtherCountries);
            if (normalized is null)
                return Problem(title: "Некорректный номер телефона", statusCode: StatusCodes.Status400BadRequest);

            // Смена номера сбрасывает подтверждение — новый номер нужно верифицировать заново.
            if (normalized != user.PhoneE164)
            {
                user.PhoneE164 = normalized;
                user.PhoneVerified = false;
            }
        }

        var now = DateTimeOffset.UtcNow;
        p.UpdatedAt = now;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return Ok(MapMe(user));
    }

    /// <summary>
    /// Загрузка аватара. Пока минимальная (валидация типа по содержимому + размер,
    /// серверный ключ, сохранение в объектном хранилище). Полная обработка
    /// (ресайз, снятие EXIF, WebP, presigned URL) — шаг 8; заменит эту реализацию.
    /// </summary>
    [HttpPost("avatar")]
    [RequestSizeLimit(MaxAvatarBytes)]
    public async Task<ActionResult<AvatarResponse>> UploadAvatar(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Problem(title: "Файл не передан", statusCode: StatusCodes.Status400BadRequest);
        if (file.Length > MaxAvatarBytes)
            return Problem(title: "Файл больше 5 МБ", statusCode: StatusCodes.Status400BadRequest);

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        // Тип определяем ПО СОДЕРЖИМОМУ (magic bytes), не по расширению/Content-Type.
        var detected = DetectImage(bytes);
        if (detected is null)
            return Problem(title: "Поддерживаются только JPEG, PNG, WebP",
                statusCode: StatusCodes.Status400BadRequest);
        var (ext, contentType) = detected.Value;

        var userId = CurrentUserId()!.Value;
        var profile = await db.Profiles.FirstOrDefaultAsync(pr => pr.UserId == userId, ct);
        if (profile is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        // Имя файла из запроса не используется — ключ генерирует сервер.
        var key = $"avatars/{userId}/{Guid.CreateVersion7()}.{ext}";
        ms.Position = 0;
        await storage.PutAsync(key, ms, ms.Length, contentType, ct);

        profile.AvatarUrl = key;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new AvatarResponse(key));
    }

    [HttpDelete]
    public async Task<IActionResult> DeleteMe(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var user = await LoadAsync(ct);
        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        var now = DateTimeOffset.UtcNow;

        // Всё в одной транзакции: анонимизация + архивация объявлений + отзыв токенов.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        user.IsDeleted = true;
        user.Email = $"deleted-{user.Id}@invalid";
        user.PhoneE164 = null;
        user.PhoneVerified = false;
        user.EmailVerified = false;
        user.SecurityStamp = Guid.NewGuid();
        user.UpdatedAt = now;

        if (user.Profile is not null)
        {
            user.Profile.DisplayName = "Удалённый пользователь";
            user.Profile.TelegramUsername = null;
            user.Profile.AvatarUrl = null;
            user.Profile.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);

        await db.Listings
            .Where(l => l.OwnerId == userId && l.Status == ListingStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ListingStatus.Archived)
                .SetProperty(l => l.UpdatedAt, now), ct);

        await refreshTokens.RevokeAllAsync(userId, ct);

        await tx.CommitAsync(ct);

        // Немедленно инвалидируем кэш — токены удалённого перестают работать сразу.
        securityStamp.Invalidate(userId);
        return NoContent();
    }

    // ---- helpers ----

    private Task<User?> LoadAsync(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        return db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    private static MeResponse MapMe(User u) => new(
        u.Id, u.Email, u.Role, u.PhoneE164, u.PhoneVerified, u.EmailVerified,
        u.IsBanned, u.BannedUntil, u.IsDeleted,
        u.Profile?.DisplayName ?? "", u.Profile?.City ?? default,
        u.Profile?.AvatarUrl, u.Profile?.TelegramUsername,
        u.Profile?.ViberEnabled ?? false, u.Profile?.WhatsappEnabled ?? false,
        u.Profile?.ShowPhoneInListing ?? true,
        u.CreatedAt, u.UpdatedAt);

    private static (string Ext, string ContentType)? DetectImage(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return ("jpg", "image/jpeg");
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return ("png", "image/png");
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
            return ("webp", "image/webp");
        return null;
    }
}
