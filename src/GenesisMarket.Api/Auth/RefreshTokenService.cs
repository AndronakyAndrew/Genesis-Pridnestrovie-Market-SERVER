using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
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
    DateTimeOffset? ExpiresAt);

public interface IRefreshTokenService
{
    Task<(string RawToken, DateTimeOffset ExpiresAt)> IssueAsync(Guid userId, string? ip, CancellationToken ct);
    Task<RefreshOutcome> RotateAsync(string rawToken, string? ip, CancellationToken ct);
    Task<bool> RevokeAsync(string rawToken, CancellationToken ct);
    Task RevokeAllAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Refresh-токены: 32 случайных байта (base64url наружу), в БД — SHA-256 хеш.
/// Ротация при каждом обновлении; повторное использование отозванного токена
/// трактуется как кража — отзывается вся цепочка токенов пользователя.
/// </summary>
public sealed class RefreshTokenService(
    AppDbContext db,
    IIpHasher ipHasher,
    IOptions<JwtOptions> options,
    ILogger<RefreshTokenService> logger) : IRefreshTokenService
{
    private readonly int _lifetimeDays = options.Value.RefreshTokenDays;

    public async Task<(string RawToken, DateTimeOffset ExpiresAt)> IssueAsync(
        Guid userId, string? ip, CancellationToken ct)
    {
        var (raw, hash) = Generate();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(_lifetimeDays);

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = hash,
            ExpiresAt = expiresAt,
            CreatedByIpHash = ipHasher.Hash(ip)
        });
        await db.SaveChangesAsync(ct);

        return (raw, expiresAt);
    }

    public async Task<RefreshOutcome> RotateAsync(string rawToken, string? ip, CancellationToken ct)
    {
        var hash = Hash(rawToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null)
            return new RefreshOutcome(RefreshStatus.Invalid, Guid.Empty, null, null);

        // Повторное использование уже отозванного токена = признак кражи.
        if (token.RevokedAt is not null)
        {
            await RevokeAllAsync(token.UserId, ct);
            logger.LogWarning(
                "Security: повторное использование отозванного refresh-токена. UserId={UserId}. Цепочка отозвана.",
                token.UserId);
            return new RefreshOutcome(RefreshStatus.Reuse, token.UserId, null, null);
        }

        if (DateTimeOffset.UtcNow >= token.ExpiresAt)
            return new RefreshOutcome(RefreshStatus.Invalid, Guid.Empty, null, null);

        // Активный токен — ротация.
        var (raw, newHash) = Generate();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(_lifetimeDays);
        var newToken = new RefreshToken
        {
            UserId = token.UserId,
            TokenHash = newHash,
            ExpiresAt = expiresAt,
            CreatedByIpHash = ipHasher.Hash(ip)
        };
        db.RefreshTokens.Add(newToken);

        token.RevokedAt = DateTimeOffset.UtcNow;
        token.ReplacedByTokenId = newToken.Id;

        await db.SaveChangesAsync(ct);
        return new RefreshOutcome(RefreshStatus.Ok, token.UserId, raw, expiresAt);
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
