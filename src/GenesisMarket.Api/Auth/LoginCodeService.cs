using System.Security.Cryptography;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Auth;

/// <summary>
/// Вход по одноразовому коду на почту — альтернатива паролю.
///
/// Запрос анонимный, поэтому наружу не утекает, существует ли адрес: как и
/// <see cref="PasswordResetService.RequestAsync"/>, <see cref="RequestAsync"/>
/// молча ничего не делает для неизвестного адреса, для кулдауна и для сбоя
/// отправки. Проверка кода отдаёт одно и то же «неверно» на любой промах.
///
/// Коды живут в той же таблице, что и коды подтверждения, но в своём канале
/// <see cref="VerificationChannel.LoginEmail"/>: хранится только хеш, срок,
/// лимит попыток и кулдаун — из <see cref="VerificationOptions"/>.
/// </summary>
public sealed class LoginCodeService(
    AppDbContext db,
    IVerificationSender sender,
    IOptions<VerificationOptions> options,
    ILogger<LoginCodeService> logger)
{
    private const VerificationChannel Channel = VerificationChannel.LoginEmail;
    private readonly VerificationOptions _o = options.Value;

    public async Task RequestAsync(string normalizedEmail, CancellationToken ct)
    {
        var user = await db.Users
            .Where(u => u.Email == normalizedEmail)
            .Select(u => new { u.Id, u.Email })
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return; // адрес неизвестен — наружу этого не показываем

        // Кулдаун защищает чужой ящик от «бомбардировки» письмами.
        var lastCreatedAt = await db.VerificationCodes
            .Where(c => c.UserId == user.Id && c.Channel == Channel)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => (DateTimeOffset?)c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var now = DateTimeOffset.UtcNow;
        if (lastCreatedAt is { } last && now - last < TimeSpan.FromSeconds(_o.ResendCooldownSeconds))
            return;

        // Активным остаётся только последний код: прошлые гасим.
        await db.VerificationCodes
            .Where(c => c.UserId == user.Id && c.Channel == Channel && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);

        var code = VerificationService.GenerateCode(_o.CodeLength);
        db.VerificationCodes.Add(new VerificationCode
        {
            UserId = user.Id,
            Channel = Channel,
            Target = user.Email,
            CodeHash = VerificationService.Hash(code),
            ExpiresAt = now.AddMinutes(_o.CodeTtlMinutes)
        });
        await db.SaveChangesAsync(ct);

        try
        {
            await sender.SendCodeAsync(Channel, user.Email, code, _o.CodeTtlMinutes, ct);
        }
        catch (Exception ex)
        {
            // Сбой отправки наружу не отдаём: 500 только для существующих адресов
            // сам по себе был бы оракулом на перечисление пользователей.
            logger.LogError(ex, "Не удалось отправить код входа. UserId={UserId}", user.Id);
        }
    }

    /// <summary>
    /// Проверяет код и гасит его. Возвращает пользователя (с профилем) при успехе,
    /// иначе <c>null</c> — причину промаха наружу не различаем.
    /// Код, пришедший на почту, заодно подтверждает владение ею.
    /// </summary>
    public async Task<User?> VerifyAsync(string normalizedEmail, string code, CancellationToken ct)
    {
        var user = await db.Users
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);
        if (user is null)
            return null;

        var entry = await db.VerificationCodes
            .Where(c => c.UserId == user.Id && c.Channel == Channel && c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (entry is null
            || DateTimeOffset.UtcNow >= entry.ExpiresAt
            || entry.Attempts >= _o.MaxAttempts)
            return null;

        if (!CryptographicOperations.FixedTimeEquals(VerificationService.Hash(code), entry.CodeHash))
        {
            entry.Attempts++;
            await db.SaveChangesAsync(ct);
            return null;
        }

        entry.ConsumedAt = DateTimeOffset.UtcNow;
        user.EmailVerified = true;
        await db.SaveChangesAsync(ct);
        return user;
    }
}
