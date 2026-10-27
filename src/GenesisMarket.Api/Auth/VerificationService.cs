using System.Security.Cryptography;
using System.Text;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Auth;

public enum SendStatus { Ok, AlreadyVerified, NoTarget, Cooldown, ChannelUnavailable }
public enum VerifyStatus { Ok, AlreadyVerified, NoCode, Expired, TooManyAttempts, Invalid }

public sealed record SendResult(SendStatus Status, DateTimeOffset? ExpiresAt = null, int RetryAfterSeconds = 0);
public sealed record VerifyResult(VerifyStatus Status);

/// <summary>
/// Единый механизм кодов подтверждения для почты и телефона: генерация,
/// хранение только хеша, кулдаун, лимит попыток, выставление нужного флага.
/// </summary>
public sealed class VerificationService(
    AppDbContext db,
    IVerificationSender sender,
    IOptions<VerificationOptions> options)
{
    private readonly VerificationOptions _o = options.Value;

    public async Task<SendResult> SendAsync(Guid userId, VerificationChannel channel, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return new SendResult(SendStatus.NoTarget);

        if (IsVerified(user, channel))
            return new SendResult(SendStatus.AlreadyVerified);

        var target = TargetOf(user, channel);
        if (string.IsNullOrEmpty(target))
            return new SendResult(SendStatus.NoTarget);

        return await SendToTargetAsync(userId, channel, target, ct);
    }

    /// <summary>
    /// Тот же кодовый конвейер для адресов, которые ещё не записаны в users
    /// (например, новый email при смене адреса).
    /// </summary>
    public async Task<SendResult> SendToTargetAsync(
        Guid userId, VerificationChannel channel, string target, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && !u.IsDeleted, ct))
            return new SendResult(SendStatus.NoTarget);

        // Кулдаун на повторную отправку по этому каналу.
        var last = await db.VerificationCodes
            .Where(c => c.UserId == userId && c.Channel == channel)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (last is not null)
        {
            var elapsed = DateTimeOffset.UtcNow - last.CreatedAt;
            var cooldown = TimeSpan.FromSeconds(_o.ResendCooldownSeconds);
            if (elapsed < cooldown)
                return new SendResult(SendStatus.Cooldown,
                    RetryAfterSeconds: (int)Math.Ceiling((cooldown - elapsed).TotalSeconds));
        }

        var now = DateTimeOffset.UtcNow;
        var code = GenerateCode(_o.CodeLength);
        var expiresAt = now.AddMinutes(_o.CodeTtlMinutes);

        // Активен только последний код в канале. Это исключает повторное
        // использование ранее отправленного кода после нового запроса.
        await db.VerificationCodes
            .Where(c => c.UserId == userId && c.Channel == channel && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);

        db.VerificationCodes.Add(new VerificationCode
        {
            UserId = userId,
            Channel = channel,
            Target = target,
            CodeHash = Hash(code),
            ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync(ct);

        try
        {
            await sender.SendCodeAsync(channel, target, code, _o.CodeTtlMinutes, ct);
        }
        catch (VerificationChannelUnavailableException)
        {
            // Канал не настроен (например, нет SMS-провайдера). Сохранённый код
            // никому не отправлен и просто истечёт; наружу — честное «недоступно»,
            // а не 500 и не молчаливый успех с кодом в логе.
            return new SendResult(SendStatus.ChannelUnavailable);
        }

        return new SendResult(SendStatus.Ok, expiresAt);
    }

    public async Task<VerifyResult> VerifyAsync(
        Guid userId, VerificationChannel channel, string code, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return new VerifyResult(VerifyStatus.NoCode);

        if (IsVerified(user, channel))
            return new VerifyResult(VerifyStatus.AlreadyVerified);

        var target = TargetOf(user, channel);
        if (string.IsNullOrEmpty(target))
            return new VerifyResult(VerifyStatus.NoCode);

        return await VerifyTargetAsync(userId, channel, target, code, markVerified: true, ct);
    }

    /// <summary>Проверить одноразовый код для заданного адреса/цели.</summary>
    public Task<VerifyResult> VerifyTargetAsync(
        Guid userId, VerificationChannel channel, string target, string code, CancellationToken ct) =>
        VerifyTargetAsync(userId, channel, target, code, markVerified: false, ct);

    /// <summary>Погасить все ещё действующие коды выбранных каналов.</summary>
    public Task InvalidateAsync(Guid userId, CancellationToken ct, params VerificationChannel[] channels)
    {
        var now = DateTimeOffset.UtcNow;
        return db.VerificationCodes
            .Where(c => c.UserId == userId && channels.Contains(c.Channel) && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
    }

    private async Task<VerifyResult> VerifyTargetAsync(
        Guid userId, VerificationChannel channel, string target, string code, bool markVerified, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);
        if (user is null)
            return new VerifyResult(VerifyStatus.NoCode);

        if (markVerified && IsVerified(user, channel))
            return new VerifyResult(VerifyStatus.AlreadyVerified);

        var entry = await db.VerificationCodes
            .Where(c => c.UserId == userId && c.Channel == channel && c.Target == target && c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (entry is null)
            return new VerifyResult(VerifyStatus.NoCode);
        if (DateTimeOffset.UtcNow >= entry.ExpiresAt)
            return new VerifyResult(VerifyStatus.Expired);
        if (entry.Attempts >= _o.MaxAttempts)
            return new VerifyResult(VerifyStatus.TooManyAttempts);

        if (!CryptographicOperations.FixedTimeEquals(Hash(code), entry.CodeHash))
        {
            await db.VerificationCodes
                .Where(c => c.Id == entry.Id && c.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => c.Attempts + 1), ct);
            return new VerifyResult(VerifyStatus.Invalid);
        }

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var consumed = await db.VerificationCodes
            .Where(c => c.Id == entry.Id && c.ConsumedAt == null && c.ExpiresAt > now && c.Attempts < _o.MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
        if (consumed == 0)
            return new VerifyResult(VerifyStatus.NoCode);

        if (markVerified)
            SetVerified(user, channel);
        if (markVerified)
            await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new VerifyResult(VerifyStatus.Ok);
    }

    private static bool IsVerified(User u, VerificationChannel ch) => ch switch
    {
        VerificationChannel.Email => u.EmailVerified,
        VerificationChannel.Phone => u.PhoneVerified,
        _ => false
    };

    private static string? TargetOf(User u, VerificationChannel ch) =>
        ch switch
        {
            VerificationChannel.Email => u.Email,
            VerificationChannel.Phone => u.PhoneE164,
            _ => null
        };

    private static void SetVerified(User u, VerificationChannel ch)
    {
        if (ch == VerificationChannel.Email) u.EmailVerified = true;
        else if (ch == VerificationChannel.Phone) u.PhoneVerified = true;
    }

    internal static string GenerateCode(int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append((char)('0' + RandomNumberGenerator.GetInt32(0, 10)));
        return sb.ToString();
    }

    internal static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()));
}
