using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Auth.TwoFactor;

public sealed record MfaChallengeTicket(string Token, DateTimeOffset ExpiresAt);

public sealed record MfaChallengeOutcome(Guid UserId, SecondFactorResult Method);

/// <summary>
/// Незавершённый вход. Первый фактор (пароль или код из письма) даёт не токены, а
/// одноразовый билет на ввод второго фактора: короткий срок и лимит попыток на билет
/// закрывают перебор 6-значного кода, а новый вход паролем не обнуляет счётчик —
/// его держит общий лимит входа по (IP, email).
/// </summary>
public sealed class MfaChallengeService(
    AppDbContext db,
    TwoFactorService twoFactor,
    IOptions<TwoFactorOptions> options,
    TimeProvider clock)
{
    private readonly TwoFactorOptions _o = options.Value;

    public async Task<MfaChallengeTicket> CreateAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // Прежние незавершённые входы этого пользователя гасятся: живёт один билет.
        await db.MfaChallenges
            .Where(c => c.UserId == userId && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);

        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var expires = now.AddMinutes(_o.ChallengeMinutes);
        db.MfaChallenges.Add(new MfaChallenge { UserId = userId, TokenHash = Hash(raw), ExpiresAt = expires });
        await db.SaveChangesAsync(ct);

        return new MfaChallengeTicket(raw, expires);
    }

    /// <summary>
    /// Проверить второй фактор по билету. null — билет недействителен или код неверен.
    /// Результат с <see cref="SecondFactorResult.Locked"/> — НЕ успех: второй фактор
    /// аккаунта временно заблокирован, сессию выдавать нельзя.
    /// </summary>
    public async Task<MfaChallengeOutcome?> CompleteAsync(string token, string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var hash = Hash(token);
        var now = clock.GetUtcNow();
        var challenge = await db.MfaChallenges.FirstOrDefaultAsync(c => c.TokenHash == hash, ct);

        if (challenge is null || challenge.ConsumedAt is not null || challenge.ExpiresAt <= now ||
            challenge.Attempts >= _o.ChallengeMaxAttempts)
            return null;

        var result = await twoFactor.VerifyAsync(challenge.UserId, code, ct);

        // Второй фактор аккаунта заблокирован после серии неверных кодов: код не проверялся,
        // попытку билета не тратим. Вызывающий отвечает 429, а не «неверный код».
        if (result == SecondFactorResult.Locked)
            return new MfaChallengeOutcome(challenge.UserId, SecondFactorResult.Locked);

        if (result is not (SecondFactorResult.Totp or SecondFactorResult.RecoveryCode))
        {
            challenge.Attempts++;
            // Исчерпанный билет гасится сразу: дальше только новый вход паролем.
            if (challenge.Attempts >= _o.ChallengeMaxAttempts)
                challenge.ConsumedAt = now;
            await db.SaveChangesAsync(ct);
            return null;
        }

        // Условное погашение: из двух одновременных подтверждений одного билета
        // токены получит только одно.
        var consumed = await db.MfaChallenges
            .Where(c => c.Id == challenge.Id && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);

        return consumed == 1 ? new MfaChallengeOutcome(challenge.UserId, result) : null;
    }

    private static byte[] Hash(string raw) => SHA256.HashData(Encoding.UTF8.GetBytes(raw));
}
