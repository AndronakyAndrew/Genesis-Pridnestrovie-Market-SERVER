using System.Security.Cryptography;
using System.Text;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Auth;

public enum SendStatus { Ok, AlreadyVerified, NoTarget, Cooldown }
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

        var code = GenerateCode(_o.CodeLength);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_o.CodeTtlMinutes);

        db.VerificationCodes.Add(new VerificationCode
        {
            UserId = userId,
            Channel = channel,
            Target = target,
            CodeHash = Hash(code),
            ExpiresAt = expiresAt
        });
        await db.SaveChangesAsync(ct);

        await sender.SendCodeAsync(channel, target, code, _o.CodeTtlMinutes, ct);
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

        var entry = await db.VerificationCodes
            .Where(c => c.UserId == userId && c.Channel == channel && c.ConsumedAt == null)
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
            entry.Attempts++;
            await db.SaveChangesAsync(ct);
            return new VerifyResult(VerifyStatus.Invalid);
        }

        SetVerified(user, channel);
        entry.ConsumedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new VerifyResult(VerifyStatus.Ok);
    }

    private static bool IsVerified(User u, VerificationChannel ch) =>
        ch == VerificationChannel.Email ? u.EmailVerified : u.PhoneVerified;

    private static string? TargetOf(User u, VerificationChannel ch) =>
        ch == VerificationChannel.Email ? u.Email : u.PhoneE164;

    private static void SetVerified(User u, VerificationChannel ch)
    {
        if (ch == VerificationChannel.Email) u.EmailVerified = true;
        else u.PhoneVerified = true;
    }

    private static string GenerateCode(int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append((char)('0' + RandomNumberGenerator.GetInt32(0, 10)));
        return sb.ToString();
    }

    private static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()));
}
