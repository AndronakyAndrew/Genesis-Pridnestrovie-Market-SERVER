namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Refresh-токен. В БД хранится только SHA-256 хеш (<see cref="TokenHash"/>),
/// сам токен наружу отдаётся один раз и в базе не лежит.
/// Ротация: при обновлении старый помечается <see cref="RevokedAt"/> +
/// <see cref="ReplacedByTokenId"/>. Повторное использование отозванного токена —
/// признак кражи (отзыв всей цепочки).
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>SHA-256 (32 байта) от исходного токена.</summary>
    public required byte[] TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Токен, которым заменён этот при ротации (цепочка).</summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>HMAC-SHA256 от IP автора (сырой IP в базе не хранится).</summary>
    public string? CreatedByIpHash { get; set; }

    public bool IsActive => RevokedAt is null && DateTimeOffset.UtcNow < ExpiresAt;
}
