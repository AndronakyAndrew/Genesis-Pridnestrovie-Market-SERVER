using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using GenesisMarket.Api.Security;
using GenesisMarket.Api.Seo;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Auth;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Auth;

public enum ResetStatus { Ok, Invalid, Expired, Used }

/// <summary>
/// Восстановление пароля по одноразовой ссылке из письма.
///
/// Запрос анонимный, поэтому наружу не должно утекать, существует ли адрес:
/// <see cref="RequestAsync"/> ничего не возвращает и молча ничего не делает для
/// неизвестного адреса, для кулдауна и для сбоя отправки. Единственный видимый
/// эффект — одинаковый ответ контроллера.
/// </summary>
public sealed class PasswordResetService(
    AppDbContext db,
    IEmailSender email,
    PasswordResetEmailRenderer renderer,
    IPasswordHasher hasher,
    IRefreshTokenService refreshTokens,
    SecurityStampValidator securityStamp,
    ISecurityAudit securityAudit,
    IIpHasher ipHasher,
    IOptions<PasswordResetOptions> options,
    IOptions<SeoOptions> seo,
    ILogger<PasswordResetService> logger)
{
    private readonly PasswordResetOptions _o = options.Value;

    public async Task RequestAsync(string rawEmail, string? ip, CancellationToken ct)
    {
        var normalized = rawEmail.Trim().ToLowerInvariant();

        var user = await db.Users
            .Where(u => u.Email == normalized)
            .Select(u => new { u.Id, u.Email })
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return; // адрес неизвестен — наружу этого не показываем

        // Кулдаун на повторное письмо: защищает почтовый ящик от «бомбардировки»
        // через чужой адрес. IP-лимит (SensitiveAnon) от этого не спасает.
        var lastCreatedAt = await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => (DateTimeOffset?)t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (lastCreatedAt is { } last
            && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(_o.ResendCooldownSeconds))
            return;

        // Прошлые невостребованные ссылки гасим: активной остаётся одна, последняя.
        var now = DateTimeOffset.UtcNow;
        await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), ct);

        var (raw, hash) = Generate();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = hash,
            ExpiresAt = now.AddMinutes(_o.TokenTtlMinutes),
            RequestedByIpHash = ipHasher.Hash(ip)
        });
        await db.SaveChangesAsync(ct);

        var mail = renderer.RenderResetEmail(BuildResetUrl(raw), _o.TokenTtlMinutes);
        try
        {
            await email.SendAsync(user.Email, mail.Subject, mail.Html, mail.Text, mail.Logo, ct);
        }
        catch (Exception ex)
        {
            // Сбой SMTP наружу не отдаём: 500 только для существующих адресов
            // сам по себе был бы оракулом на перечисление пользователей.
            logger.LogError(ex, "Не удалось отправить письмо восстановления пароля. UserId={UserId}", user.Id);
        }
    }

    public async Task<ResetStatus> ResetAsync(
        string rawToken, string newPassword, CancellationToken ct)
    {
        var hash = Hash(rawToken);
        var entry = await db.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (entry?.User is null)
            return ResetStatus.Invalid;
        if (entry.ConsumedAt is not null)
            return ResetStatus.Used;
        if (DateTimeOffset.UtcNow >= entry.ExpiresAt)
            return ResetStatus.Expired;

        var user = entry.User;
        user.PasswordHash = hasher.Hash(newPassword);
        user.SecurityStamp = Guid.NewGuid();               // разлогинивает все сессии
        entry.ConsumedAt = DateTimeOffset.UtcNow;

        await refreshTokens.RevokeAllAsync(user.Id, ct);
        await db.SaveChangesAsync(ct);

        securityStamp.Invalidate(user.Id);
        securityAudit.PasswordChanged(user.Id);
        return ResetStatus.Ok;
    }

    /// <summary>
    /// Абсолютная ссылка на фронтенд. Публичный адрес не настроен (<c>Seo:WebBaseUrl</c>) —
    /// остаётся относительный путь: в Development письмо всё равно уходит в лог.
    /// </summary>
    private string BuildResetUrl(string rawToken)
    {
        var path = $"{_o.ResetPath}?token={Uri.EscapeDataString(rawToken)}";
        return seo.Value.NormalizedBaseUrl is { } baseUrl ? baseUrl + path : path;
    }

    private static (string Raw, byte[] Hash) Generate()
    {
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    private static byte[] Hash(string rawToken) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(rawToken.Trim()));
}
