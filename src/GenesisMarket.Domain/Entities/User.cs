using GenesisMarket.Domain.Common;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Учётная запись. Аутентификация — JWT + BCrypt, ASP.NET Identity не используется.
/// Публичная/презентационная часть вынесена в <see cref="Profile"/> (1:1).
/// </summary>
public class User : BaseEntity
{
    public required string Email { get; set; }

    /// <summary>BCrypt-хеш пароля. Наружу (в DTO) не отдаётся никогда.</summary>
    public required string PasswordHash { get; set; }

    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>Телефон в формате E.164. Подтверждается по SMS (из профиля).</summary>
    public string? PhoneE164 { get; set; }

    /// <summary>Телефон подтверждён по SMS.</summary>
    public bool PhoneVerified { get; set; }

    /// <summary>
    /// Почта подтверждена по коду. Анти-фрод: какой из каналов обязателен для
    /// публикации объявлений, определяется политикой (конфиг Publishing).
    /// </summary>
    public bool EmailVerified { get; set; }

    /// <summary>Меняется при смене пароля/бане — инвалидирует выданные JWT.</summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Денормализованный средний рейтинг по видимым отзывам (1..5). null — отзывов нет.
    /// Извне не редактируется: поддерживается триггером БД (<c>reviews_rating_sync</c>)
    /// в той же транзакции, что и запись/редактирование/скрытие отзыва.
    /// </summary>
    public double? AverageRating { get; private set; }

    /// <summary>
    /// Денормализованное число видимых (не скрытых) отзывов о пользователе.
    /// Поддерживается тем же триггером, что и <see cref="AverageRating"/>.
    /// </summary>
    public int ReviewsCount { get; private set; }

    /// <summary>
    /// Денормализованное число ОДОБРЕННЫХ объявлений автора: прошедших модератора либо
    /// опубликованных автоматически при полном доверии. Считает <c>listings.ApprovedAt</c>,
    /// а не факт подачи — поданное и отклонённое объявление доверия не приносит.
    /// Извне не редактируется: поддерживается триггером БД (<c>listings_trust_sync</c>)
    /// в той же транзакции, что и одобрение объявления.
    /// </summary>
    public int ApprovedListingsCount { get; private set; }

    /// <summary>
    /// Момент последнего отклонения объявления этого автора модератором. Тем же
    /// триггером, что и <see cref="ApprovedListingsCount"/>. Свежий отказ — жёсткий
    /// сигнал политике модерации: автопубликация автору временно закрыта.
    /// </summary>
    public DateTimeOffset? LastRejectedAt { get; private set; }

    public bool IsBanned { get; set; }
    public DateTimeOffset? BannedUntil { get; set; }

    /// <summary>
    /// Мягкое удаление аккаунта: строка не удаляется, а анонимизируется
    /// (email/имя/контакты обнуляются). Токены удалённого не проходят валидацию.
    /// </summary>
    public bool IsDeleted { get; set; }

    // Навигация
    public Profile? Profile { get; set; }
    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
    public ICollection<Favorite> Favorites { get; set; } = new List<Favorite>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
