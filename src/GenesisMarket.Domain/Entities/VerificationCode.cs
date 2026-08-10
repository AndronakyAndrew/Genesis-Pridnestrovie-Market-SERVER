using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Код подтверждения контакта (почты или телефона). Единый механизм для обоих
/// каналов: генерируется сервером, отправляется пользователю; в БД хранится
/// только SHA-256 хеш кода (<see cref="CodeHash"/>). Подтверждение — из профиля.
/// </summary>
public class VerificationCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Канал: почта или телефон.</summary>
    public VerificationChannel Channel { get; set; }

    /// <summary>Что подтверждаем: e-mail или телефон в E.164.</summary>
    public required string Target { get; set; }

    /// <summary>SHA-256 от числового кода. Сам код в базе не хранится.</summary>
    public required byte[] CodeHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Счётчик неверных попыток (защита от перебора кода).</summary>
    public int Attempts { get; set; }

    /// <summary>Проставляется при успешном подтверждении — код одноразовый.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsUsable => ConsumedAt is null && DateTimeOffset.UtcNow < ExpiresAt;
}
