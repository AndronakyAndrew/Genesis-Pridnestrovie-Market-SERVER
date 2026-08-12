using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Публичный профиль пользователя (1:1 с <see cref="User"/>).
/// Первичный ключ совпадает с <see cref="UserId"/> — отдельного Id нет.
/// </summary>
public class Profile
{
    /// <summary>PK и одновременно FK на users.Id.</summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Отображаемое имя. Ограничение длины ≤ 60 — на уровне БД (CHECK).</summary>
    public required string DisplayName { get; set; }

    /// <summary>Город пользователя по умолчанию.</summary>
    public City City { get; set; }

    public string? AvatarUrl { get; set; }
    public string? TelegramUsername { get; set; }

    /// <summary>
    /// Канал, которым доставлять уведомления (outbox). По умолчанию — почта:
    /// адрес есть у каждого. Telegram работает только при заполненном <see cref="TelegramChatId"/>.
    /// </summary>
    public NotificationChannel NotifyVia { get; set; } = NotificationChannel.Email;

    /// <summary>
    /// Идентификатор личного чата Telegram (не username): без него бот не может писать
    /// пользователю в ЛС. Заполняется при привязке Telegram (шаг 16). null ⇒ Telegram-доставка
    /// невозможна, канал деградирует к почте.
    /// </summary>
    public long? TelegramChatId { get; set; }

    public bool ViberEnabled { get; set; }
    public bool WhatsappEnabled { get; set; }

    /// <summary>Показывать телефон в объявлениях.</summary>
    public bool ShowPhoneInListing { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
