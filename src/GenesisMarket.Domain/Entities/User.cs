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

    public bool IsBanned { get; set; }
    public DateTimeOffset? BannedUntil { get; set; }

    // Навигация
    public Profile? Profile { get; set; }
    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
    public ICollection<Favorite> Favorites { get; set; } = new List<Favorite>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
