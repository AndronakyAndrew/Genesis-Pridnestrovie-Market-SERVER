using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using GenesisMarket.Api.Auth.Roles;
using GenesisMarket.Api.Auth.TwoFactor;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Auth;

public enum RefreshStatus
{
    Invalid,
    Reuse,
    Ok
}

public sealed record RefreshOutcome(
    RefreshStatus Status,
    Guid UserId,
    string? NewRawToken,
    DateTimeOffset? ExpiresAt,
    Guid SessionId = default,
    bool MfaVerified = false);

/// <summary>
/// Всё, что известно о клиенте в момент выдачи токена. Сырой User-Agent сюда
/// не попадает — он разбирается на границе (контроллер) и дальше не живёт.
/// </summary>
public sealed record SessionContext(string? Ip, ClientAgent Agent)
{
    public static SessionContext Empty => new(null, new ClientAgent(null, null, null));
}

public interface IRefreshTokenService
{
    /// <summary>Новая сессия. <paramref name="mfaVerified"/> — вход прошёл второй фактор.</summary>
    Task<(string RawToken, DateTimeOffset ExpiresAt, Guid SessionId)> IssueAsync(
        Guid userId, SessionContext context, bool mfaVerified, CancellationToken ct);

    Task<RefreshOutcome> RotateAsync(string rawToken, SessionContext context, CancellationToken ct);
    Task<bool> RevokeAsync(string rawToken, CancellationToken ct);
    Task RevokeAllAsync(Guid userId, CancellationToken ct);

    /// <summary>Отзыв одной сессии владельцем. false — сессии нет или она чужая.</summary>
    Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Отметить живую сессию как прошедшую 2FA — после включения 2FA в этой же сессии
    /// человек только что предъявил код, заставлять его входить заново незачем.
    /// </summary>
    Task MarkMfaVerifiedAsync(Guid userId, Guid sessionId, CancellationToken ct);
}

/// <summary>
/// Refresh-токены: 32 случайных байта (base64url наружу), в БД — SHA-256 хеш.
/// Ротация при каждом обновлении; повторное использование отозванного токена
/// трактуется как кража — отзывается вся цепочка токенов пользователя.
///
/// Служебная сессия (роль Moderator и выше) короткая: не дольше
/// <see cref="StaffSessionOptions.AbsoluteHours"/> от входа и не дольше
/// <see cref="StaffSessionOptions.IdleMinutes"/> без активности — дальше только новый вход с 2FA.
/// </summary>
public sealed class RefreshTokenService(
    AppDbContext db,
    IIpHasher ipHasher,
    IOptions<JwtOptions> options,
    IOptions<StaffSessionOptions> staffOptions,
    IRoleStore roles,
    ILogger<RefreshTokenService> logger) : IRefreshTokenService
{
    private readonly StaffSessionOptions _staff = staffOptions.Value;

    private readonly int _lifetimeDays = options.Value.RefreshTokenDays;
    private readonly TimeSpan _reuseGrace =
        TimeSpan.FromSeconds(Math.Max(0, options.Value.RefreshReuseGraceSeconds));

    public async Task<(string RawToken, DateTimeOffset ExpiresAt, Guid SessionId)> IssueAsync(
        Guid userId, SessionContext context, bool mfaVerified, CancellationToken ct)
    {
        var (raw, hash) = Generate();
        var now = DateTimeOffset.UtcNow;
        var isStaff = RolePermissions.IsStaff(await roles.GetRoleAsync(userId, ct));
        var expiresAt = ExpiryFor(isStaff, sessionStartedAt: now, now);

        var token = new RefreshToken
        {
            UserId = userId,
            TokenHash = hash,
            ExpiresAt = expiresAt,
            CreatedByIpHash = ipHasher.Hash(context.Ip),
            SessionStartedAt = now,
            LastSeenAt = now,
            DeviceFamily = context.Agent.Device,
            BrowserFamily = context.Agent.Browser,
            OsFamily = context.Agent.Os,
            IpPrefix = ClientFingerprint.IpPrefix(context.Ip),
            MfaVerifiedAt = mfaVerified ? now : null
        };
        // Новая сессия: её идентификатор — идентификатор первой строки цепочки.
        token.SessionId = token.Id;

        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync(ct);

        return (raw, expiresAt, token.SessionId);
    }

    public async Task<RefreshOutcome> RotateAsync(string rawToken, SessionContext context, CancellationToken ct)
    {
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null)
            return new RefreshOutcome(RefreshStatus.Invalid, Guid.Empty, null, null);

        if (token.RevokedAt is not null)
        {
            // Отозван ЯВНО (logout, отзыв сессии из «Безопасности», logout-all):
            // ReplacedByTokenId пуст. Токен просто мёртв — владелец сам его убил.
            // Считать это кражей нельзя: отозванное устройство ещё какое-то время
            // продолжает ходить за обновлением, и «отзыв цепочки» разлогинивал бы
            // человека со ВСЕХ устройств через несколько минут после того, как он
            // отключил одно. Тогда кнопка «выйти на этом устройстве» делала бы
            // ровно то же, что «выйти везде».
            if (token.ReplacedByTokenId is null)
                return new RefreshOutcome(RefreshStatus.Invalid, Guid.Empty, null, null);

            // Отозван РОТАЦИЕЙ (есть замена) — предъявлен старый токен из середины
            // цепочки. Обычно это кража, но есть законный случай: refresh-токен
            // лежит в одной cookie на весь браузер, и вкладки, стартовавшие
            // одновременно (в т.ч. при восстановлении сессии браузера), уходят за
            // обновлением с ОДНИМ токеном. Пока первая ротирует, вторая держит в
            // руках уже заменённый. Без окна это считалось кражей и отзывало всю
            // цепочку — человек молча вылетал на всех устройствах.
            // В пределах окна отдаём продолжение той же цепочки, за окном — кража.
            if (DateTimeOffset.UtcNow - token.RevokedAt.Value <= _reuseGrace)
            {
                var head = await db.RefreshTokens.FirstOrDefaultAsync(
                    t => t.SessionId == token.SessionId && t.RevokedAt == null, ct);

                if (head is not null && DateTimeOffset.UtcNow < head.ExpiresAt)
                    return await RotateActiveAsync(head, context, ct);
            }

            await RevokeAllAsync(token.UserId, ct);
            logger.LogWarning(
                "Security: повторное использование отозванного refresh-токена. UserId={UserId}. Цепочка отозвана.",
                token.UserId);
            return new RefreshOutcome(RefreshStatus.Reuse, token.UserId, null, null);
        }

        if (DateTimeOffset.UtcNow >= token.ExpiresAt)
            return new RefreshOutcome(RefreshStatus.Invalid, Guid.Empty, null, null);

        return await RotateActiveAsync(token, context, ct);
    }

    /// <summary>Замена активного токена цепочки новым. Вызывается и обычным путём,
    /// и из окна повторного предъявления — правила выдачи должны быть одни.</summary>
    private async Task<RefreshOutcome> RotateActiveAsync(
        RefreshToken token, SessionContext context, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var isStaff = RolePermissions.IsStaff(await roles.GetRoleAsync(token.UserId, ct));

        // Служебная сессия: по истечении абсолютного срока или после простоя — только
        // новый вход. Сессия гасится целиком, а не просто не обновляется: иначе
        // устройство продолжало бы числиться активным в «Безопасности».
        if (isStaff && StaffSessionExpired(token, now))
        {
            await RevokeSessionAsync(token.UserId, token.SessionId, ct);
            return new RefreshOutcome(RefreshStatus.Invalid, Guid.Empty, null, null);
        }

        var (raw, newHash) = Generate();
        var expiresAt = ExpiryFor(isStaff, token.SessionStartedAt, now);
        var newToken = new RefreshToken
        {
            UserId = token.UserId,
            TokenHash = newHash,
            ExpiresAt = expiresAt,
            CreatedByIpHash = ipHasher.Hash(context.Ip),

            // Сессия переживает ротацию: идентификатор и время входа переносятся,
            // иначе устройство выглядело бы как новое каждые 15 минут.
            SessionId = token.SessionId,
            SessionStartedAt = token.SessionStartedAt,
            LastSeenAt = now,

            // Семейства перечитываем: браузер мог обновиться, а адрес — смениться
            // (мобильная сеть). Если заголовка нет — оставляем прежние значения,
            // чтобы сессия не «обнулилась» из-за запроса без User-Agent.
            DeviceFamily = context.Agent.Device ?? token.DeviceFamily,
            BrowserFamily = context.Agent.Browser ?? token.BrowserFamily,
            OsFamily = context.Agent.Os ?? token.OsFamily,
            IpPrefix = ClientFingerprint.IpPrefix(context.Ip) ?? token.IpPrefix,

            // Пройденный второй фактор — свойство сессии, а не строки цепочки.
            MfaVerifiedAt = token.MfaVerifiedAt
        };
        db.RefreshTokens.Add(newToken);

        token.RevokedAt = now;
        token.ReplacedByTokenId = newToken.Id;

        await db.SaveChangesAsync(ct);
        return new RefreshOutcome(RefreshStatus.Ok, token.UserId, raw, expiresAt, newToken.SessionId,
            MfaVerified: newToken.MfaVerifiedAt is not null);
    }

    public async Task MarkMfaVerifiedAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.SessionId == sessionId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.MfaVerifiedAt, now), ct);
    }

    /// <summary>Срок refresh-токена: обычный — скользящий, служебный — не дальше абсолютного срока сессии.</summary>
    private DateTimeOffset ExpiryFor(bool isStaff, DateTimeOffset sessionStartedAt, DateTimeOffset now)
    {
        var regular = now.AddDays(_lifetimeDays);
        if (!isStaff)
            return regular;

        var absolute = sessionStartedAt.AddHours(_staff.AbsoluteHours);
        return absolute < regular ? absolute : regular;
    }

    private bool StaffSessionExpired(RefreshToken token, DateTimeOffset now) =>
        now >= token.SessionStartedAt.AddHours(_staff.AbsoluteHours) ||
        now >= (token.LastSeenAt ?? token.CreatedAt).AddMinutes(_staff.IdleMinutes);

    public async Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Фильтр по UserId — в самом запросе: чужая сессия не отзывается и не
        // подтверждается, вызывающий получит тот же ответ, что и для несуществующей.
        var affected = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.SessionId == sessionId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        return affected > 0;
    }

    public async Task<bool> RevokeAsync(string rawToken, CancellationToken ct)
    {
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.RevokedAt is not null)
            return false;

        token.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task RevokeAllAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private static (string Raw, byte[] Hash) Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var raw = Base64Url.EncodeToString(bytes);
        return (raw, Hash(raw));
    }

    private static byte[] Hash(string rawToken) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
}
