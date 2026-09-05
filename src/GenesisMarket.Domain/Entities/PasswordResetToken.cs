namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Одноразовый токен восстановления пароля. В БД лежит только SHA-256 хеш
/// (<see cref="TokenHash"/>) — сам токен уходит один раз, ссылкой в письме.
/// Отличается от <see cref="VerificationCode"/> тем, что выдаётся анонимно
/// (пользователь не вошёл) и не подтверждает контакт, а разрешает смену пароля.
/// </summary>
public class PasswordResetToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>SHA-256 (32 байта) от исходного токена.</summary>
    public required byte[] TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Проставляется при успешной смене пароля — токен одноразовый.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>HMAC-SHA256 от IP запросившего (сырой IP в базе не хранится).</summary>
    public string? RequestedByIpHash { get; set; }

    public bool IsUsable => ConsumedAt is null && DateTimeOffset.UtcNow < ExpiresAt;
}
