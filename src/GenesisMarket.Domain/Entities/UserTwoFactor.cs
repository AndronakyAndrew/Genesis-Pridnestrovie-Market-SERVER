namespace GenesisMarket.Domain.Entities;

/// <summary>
/// TOTP (RFC 6238) пользователя. 1:1 с <see cref="User"/>, PK = FK. Строка появляется
/// при начале настройки (<see cref="EnabledAt"/> = null) и становится рабочей после
/// подтверждения первым кодом. Секрет хранится зашифрованным (AES-GCM, ключ из
/// окружения) — дамп базы без ключа второй фактор не раскрывает.
/// </summary>
public class UserTwoFactor
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>nonce(12) ‖ tag(16) ‖ ciphertext — зашифрованный секрет TOTP.</summary>
    public required byte[] SecretCipher { get; set; }

    /// <summary>Когда 2FA включена. null — настройка начата, но не подтверждена кодом.</summary>
    public DateTimeOffset? EnabledAt { get; set; }

    /// <summary>
    /// Последний принятый шаг времени (unixtime / 30). Код того же или более раннего
    /// шага повторно не принимается — перехваченный код нельзя предъявить второй раз.
    /// </summary>
    public long LastUsedTimeStep { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Неудачные проверки второго фактора подряд — по аккаунту, а не по IP: перебор с
    /// многих адресов при известном пароле упирается в этот счётчик. Успех обнуляет.
    /// </summary>
    public int FailedAttempts { get; set; }

    /// <summary>До какого момента второй фактор не принимается вовсе (даже верный код).</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public ICollection<RecoveryCode> RecoveryCodes { get; set; } = new List<RecoveryCode>();

    public bool IsEnabled => EnabledAt is not null;
}

/// <summary>
/// Резервный код восстановления 2FA: одноразовый, хранится HMAC-хешем. Выдаётся пачкой
/// при включении 2FA и при перевыпуске (старая пачка удаляется целиком).
/// </summary>
public class RecoveryCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    /// <summary>HMAC-SHA256 нормализованного кода.</summary>
    public required byte[] CodeHash { get; set; }

    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Незавершённый вход: пароль (или код из письма) принят, ждём второй фактор.
/// Сырой токен отдаётся клиенту один раз, в БД — SHA-256. Живёт минуты, число
/// попыток ограничено — перебор TOTP по одному challenge невозможен.
/// </summary>
public class MfaChallenge
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public required byte[] TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
