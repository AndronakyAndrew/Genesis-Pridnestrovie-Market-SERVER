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

    // ---- Сессия (вкладка «Безопасность» в профиле) ----

    /// <summary>
    /// Устойчивый идентификатор сессии: не меняется при ротации, в отличие от
    /// <see cref="Id"/>. У первой строки цепочки равен <see cref="Id"/>, дальше
    /// переносится. Без него «сессия» жила бы ≤15 минут: каждая ротация создаёт
    /// новую строку, и и возраст сессии, и её id в UI сбрасывались бы.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Когда сессия началась (вход с устройства). Переносится по цепочке ротации,
    /// в отличие от <see cref="CreatedAt"/> — тот относится к конкретной строке.
    /// </summary>
    public DateTimeOffset SessionStartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Последняя активность сессии. Обновляется не чаще раза в 5 минут
    /// (см. SessionActivityMiddleware): иначе каждый запрос API — запись в БД.
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// Тип устройства: «Компьютер» / «Телефон» / «Планшет». Разбирается из
    /// User-Agent при выдаче токена; сам User-Agent НЕ сохраняется — сырая строка
    /// с версиями сборок опознаёт устройство точнее, чем нужно для этой задачи.
    /// </summary>
    public string? DeviceFamily { get; set; }

    /// <summary>Семейство браузера («Chrome», «Safari»), без версии.</summary>
    public string? BrowserFamily { get; set; }

    /// <summary>Семейство ОС («Windows», «Android»), без версии.</summary>
    public string? OsFamily { get; set; }

    /// <summary>
    /// Первые два октета IPv4 («185.112») либо два хекстета IPv6 («2a02:6b8»).
    /// Единственная часть адреса, лежащая в открытом виде: её хватает, чтобы
    /// владелец узнал «свою» сеть, и мало, чтобы получился журнал перемещений.
    /// Полный адрес — только как HMAC в <see cref="CreatedByIpHash"/>.
    /// </summary>
    public string? IpPrefix { get; set; }

    public bool IsActive => RevokedAt is null && DateTimeOffset.UtcNow < ExpiresAt;
}
