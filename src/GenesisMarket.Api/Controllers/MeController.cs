using System.Text.Json;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Profiles;
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

        if (request.Description is not null)
        {
            // TODO (до запуска рекламы): «О себе» — свободный пользовательский текст,
            // видимый всем в публичном профиле и в карточке продавца, то есть такая же
            // площадка для спама/оскорблений/контактов в обход площадки, как и текст
            // объявления — но без премодерации: ListingModerationPolicy решает лишь,
            // слать ли объявление в PendingReview, содержимое не проверяет, и на
            // профиль не распространяется вовсе. Как только у текста объявлений
            // появится фильтр содержимого, прогонять описание через него же —
            // одним правилом, а не отдельной веткой.
            // Экранирование HTML здесь не нужно: оно делается на границе вывода
            // (HtmlEncoder в письмах и Telegram-постах), см. комментарий в ProfileText.
            p.Description = ProfileText.NormalizeDescription(request.Description);
        }

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
    /// а аватар отдаётся анонимно (<c>GET /api/users/by-code/{code}/avatar</c>).
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
        // User нужен ради PublicCode: адрес аватара строится по нему, не по Guid.
        var profile = await db.Profiles.Include(pr => pr.User)
            .FirstOrDefaultAsync(pr => pr.UserId == userId, ct);
        if (profile is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        // Имя файла из запроса не используется — ключ генерирует сервер.
        var key = $"avatars/{userId}/{Guid.CreateVersion7()}.webp";
        await using (var os = new MemoryStream(processed.Original))
            await storage.PutAsync(key, os, os.Length, "image/webp", ct);

        // Прежний аватар: ссылку перетираем, а объект отправляем на удаление.
        // Без этого каждая повторная загрузка оставляла в хранилище сироту.
        EnqueueAvatarDeletion(profile.AvatarUrl);

        profile.AvatarUrl = key;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new AvatarResponse(
            AvatarUrl.Build(Request, profile.User!.PublicCode, profile.UpdatedAt)));
    }

    /// <summary>
    /// Удаление аватара. Идемпотентно: 204 и когда аватар был, и когда его нет —
    /// повторный клик по «Удалить» не должен давать 404.
    ///
    /// Порядок строго такой: сначала снимается ссылка в БД, и только после
    /// коммита удаляется объект. Наоборот нельзя — упавшая транзакция оставила бы
    /// профиль со ссылкой на несуществующий файл. Само удаление идёт заявкой в
    /// outbox в той же транзакции (как у фото объявлений, ListingImagesController):
    /// заявка и обнуление ссылки либо фиксируются вместе, либо не происходят вовсе,
    /// а недоступность MinIO в момент запроса не роняет ответ — диспетчер повторит.
    ///
    /// Rate-limit: как у загрузки — именованной политики нет, действует только
    /// глобальный лимит на IP (RateLimit:GlobalPerMinute). Ставить сюда отдельную
    /// политику значило бы ограничить удаление строже, чем загрузку, хотя удаление
    /// дешевле: ни разбора картинки, ни записи в хранилище, ни 5 МБ тела — одна
    /// строка UPDATE и запись в outbox.
    /// </summary>
    [HttpDelete("avatar")]
    public async Task<IActionResult> DeleteAvatar(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var profile = await db.Profiles.FirstOrDefaultAsync(pr => pr.UserId == userId, ct);
        if (profile is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        // Аватара нет — уже в нужном состоянии, БД не трогаем.
        if (profile.AvatarUrl is null)
            return NoContent();

        EnqueueAvatarDeletion(profile.AvatarUrl);
        profile.AvatarUrl = null;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>
    /// Активные сессии владельца. Только свои и только живые: отозванные и
    /// просроченные строки не показываем — в списке устройств им не место.
    ///
    /// Одна строка = одна сессия, а не один refresh-токен: ротация создаёт новую
    /// строку каждые ≤15 минут, поэтому группируем по SessionId и берём
    /// действующую. TODO (город): в дизайне рядом с IP стоит «Тирасполь, MD».
    /// Не реализовано намеренно — это требует базы GeoIP и превращает таблицу
    /// сессий в журнал перемещений владельца. City всегда null.
    /// </summary>
    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<SessionResponse>>> GetSessions(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        var currentSessionId = CurrentUser.SessionId;
        var now = DateTimeOffset.UtcNow;

        var sessions = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.LastSeenAt ?? t.SessionStartedAt)
            .Select(t => new SessionResponse(
                t.SessionId,
                t.DeviceFamily,
                t.BrowserFamily,
                t.OsFamily,
                t.IpPrefix,
                null,                       // City — см. TODO выше
                t.SessionStartedAt,
                t.LastSeenAt,
                // Текущая сессия — по claim sid, а не по «самой свежей LastSeenAt»:
                // одновременная активность на двух устройствах сделала бы такую
                // догадку неверной.
                currentSessionId != null && t.SessionId == currentSessionId))
            .ToListAsync(ct);

        return Ok(sessions);
    }

    /// <summary>
    /// Отзыв одной сессии. Чужая сессия — 404, а не 403: 403 подтверждал бы, что
    /// такой идентификатор существует.
    ///
    /// Отзыв своей текущей сессии разрешён и равносилен выходу: refresh-токен
    /// умирает сразу, access-токен доживает свой срок (≤ Jwt:AccessTokenMinutes,
    /// по умолчанию 15 минут) — ровно так же ведёт себя POST /api/auth/logout.
    /// Мгновенно убить и access-токены может только logout-all: он меняет
    /// SecurityStamp, а тот общий на все сессии.
    /// </summary>
    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;

        var revoked = await refreshTokens.RevokeSessionAsync(userId, id, ct);
        return revoked
            ? NoContent()
            : Problem(title: "Сессия не найдена", statusCode: StatusCodes.Status404NotFound);
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
            // Файл аватара раньше оставался в хранилище навсегда: ссылку снимали,
            // объект — нет. Заявка на удаление идёт в той же транзакции, что и
            // анонимизация, тем же путём, что и DELETE /api/me/avatar.
            EnqueueAvatarDeletion(user.Profile.AvatarUrl);
            user.Profile.AvatarUrl = null;
            // «О себе» — текст, написанный самим пользователем, в том числе о себе
            // лично: при удалении аккаунта он стирается вместе с контактами.
            user.Profile.Description = null;
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

    /// <summary>
    /// Ставит объект аватара в очередь на удаление из хранилища. Вызывается ДО
    /// <c>SaveChangesAsync</c>, чтобы заявка попала в ту же транзакцию, что и
    /// снятие ссылки. <c>null</c> (аватара не было) — ничего не ставим.
    ///
    /// Тип сообщения — <see cref="OutboxMessage.DeleteImages"/> (payload: массив
    /// ключей), тот же, что у фото объявлений: у аватара ключ один, но заводить
    /// ради этого отдельный тип и обработчик незачем.
    /// </summary>
    private void EnqueueAvatarDeletion(string? objectKey)
    {
        if (objectKey is null)
            return;

        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.DeleteImages,
            Payload = JsonSerializer.Serialize(new[] { objectKey })
        });
    }

    private Task<User?> LoadAsync(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        return db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    private async Task<MeResponse> MapMeAsync(User u, CancellationToken ct) => new(
        u.Id, u.PublicCode, u.Email, u.Role, u.PhoneE164, u.PhoneVerified, u.EmailVerified,
        u.IsBanned, u.BannedUntil, u.IsDeleted,
        u.Profile?.DisplayName ?? "", u.Profile?.City ?? default,
        u.Profile?.AvatarUrl is null
            ? null
            : AvatarUrl.Build(Request, u.PublicCode, u.Profile.UpdatedAt),
        u.Profile?.TelegramUsername,
        u.Profile?.Description,
        u.Profile?.ViberEnabled ?? false, u.Profile?.WhatsappEnabled ?? false,
        u.Profile?.ShowPhoneInListing ?? true,
        u.CreatedAt, u.UpdatedAt);


}
