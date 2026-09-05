using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Imaging;
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
    IOptions<PhoneOptions> phoneOptions,
    IImageProcessor imageProcessor) : ApiControllerBase
{
    private const long MaxAvatarBytes = 5 * 1024 * 1024;

    [HttpGet]
    public async Task<ActionResult<MeResponse>> GetMe(CancellationToken ct)
    {
        var user = await LoadAsync(ct);
        return user is null
            ? Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound)
            : Ok(await MapMeAsync(user, ct));
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

        return Ok(await MapMeAsync(user, ct));
    }

    /// <summary>
    /// Загрузка аватара. Проходит тот же конвейер, что и фото объявлений
    /// (<see cref="IImageProcessor"/>): тип по magic bytes, защита от decompression bomb,
    /// снятие EXIF/IPTC/XMP и перекодирование в WebP — всё ДО записи в хранилище.
    /// Снятие EXIF здесь принципиально: в метаданных снимка лежат GPS-координаты,
    /// а аватар отдаётся анонимно (<c>GET /api/users/{id}/avatar</c>).
    /// </summary>
    [HttpPost("avatar")]
    [RequestSizeLimit(MaxAvatarBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxAvatarBytes)]
    public async Task<ActionResult<AvatarResponse>> UploadAvatar(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Problem(title: "Файл не передан", statusCode: StatusCodes.Status400BadRequest);
        if (file.Length > MaxAvatarBytes)
            return Problem(title: "Файл больше 5 МБ", statusCode: StatusCodes.Status400BadRequest);

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        ProcessedImage processed;
        try
        {
            processed = await imageProcessor.ProcessAsync(ms, ct);
        }
        catch (UnsupportedImageFormatException)
        {
            return Problem(title: "Поддерживаются только JPEG, PNG, WebP",
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (ImageTooLargeException)
        {
            return Problem(title: "Изображение слишком большое",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var userId = CurrentUserId()!.Value;
        var profile = await db.Profiles.FirstOrDefaultAsync(pr => pr.UserId == userId, ct);
        if (profile is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        // Имя файла из запроса не используется — ключ генерирует сервер.
        var key = $"avatars/{userId}/{Guid.CreateVersion7()}.webp";
        await using (var os = new MemoryStream(processed.Original))
            await storage.PutAsync(key, os, os.Length, "image/webp", ct);

        profile.AvatarUrl = key;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new AvatarResponse(BuildAvatarUrl(userId, profile.UpdatedAt)));
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

    private async Task<MeResponse> MapMeAsync(User u, CancellationToken ct) => new(
        u.Id, u.Email, u.Role, u.PhoneE164, u.PhoneVerified, u.EmailVerified,
        u.IsBanned, u.BannedUntil, u.IsDeleted,
        u.Profile?.DisplayName ?? "", u.Profile?.City ?? default,
        u.Profile?.AvatarUrl is null ? null : BuildAvatarUrl(u.Id, u.Profile.UpdatedAt), u.Profile?.TelegramUsername,
        u.Profile?.ViberEnabled ?? false, u.Profile?.WhatsappEnabled ?? false,
        u.Profile?.ShowPhoneInListing ?? true,
        u.CreatedAt, u.UpdatedAt);

    private string BuildAvatarUrl(Guid userId, DateTimeOffset? updatedAt) =>
        $"{Request.Scheme}://{Request.Host}/api/users/{userId}/avatar?v={updatedAt?.UtcTicks ?? 0}";

}
