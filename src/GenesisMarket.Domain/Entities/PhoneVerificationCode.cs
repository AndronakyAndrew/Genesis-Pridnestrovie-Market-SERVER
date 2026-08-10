namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Код подтверждения телефона по SMS. Генерируется сервером, отправляется
/// пользователю; в БД хранится только SHA-256 хеш кода (<see cref="CodeHash"/>).
/// Подтверждение происходит из профиля, не при регистрации.
/// </summary>
public class PhoneVerificationCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Телефон в E.164, который подтверждается этим кодом.</summary>
    public required string Phone { get; set; }

    /// <summary>SHA-256 от числового кода. Сам код в базе не хранится.</summary>
    public required byte[] CodeHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Счётчик неверных попыток ввода (защита от перебора кода).</summary>
    public int Attempts { get; set; }

    /// <summary>Проставляется при успешном подтверждении — код одноразовый.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsUsable => ConsumedAt is null && DateTimeOffset.UtcNow < ExpiresAt;
}
