using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Приватный профиль владельца — всё, кроме PasswordHash и SecurityStamp.
/// </summary>
public record MeResponse(
    Guid Id,
    string Email,
    UserRole Role,
    string? PhoneE164,
    bool PhoneVerified,
    bool EmailVerified,
    bool IsBanned,
    DateTimeOffset? BannedUntil,
    bool IsDeleted,
    string DisplayName,
    City City,
    string? AvatarUrl,
    string? TelegramUsername,
    bool ViberEnabled,
    bool WhatsappEnabled,
    bool ShowPhoneInListing,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// PATCH профиля. ФИКСИРОВАННЫЙ набор полей — защита от mass assignment:
/// Role, IsBanned, PasswordHash, SecurityStamp, Email сюда не входят и не
/// редактируются ни при каких условиях. Все поля опциональны (обновляется
/// только переданное).
/// </summary>
public record UpdateMeRequest(
    [MinLength(2), MaxLength(60)] string? DisplayName,
    City? City,
    [MaxLength(20)] string? PhoneE164,
    [MaxLength(64)] string? TelegramUsername,
    bool? ViberEnabled,
    bool? WhatsappEnabled,
    bool? ShowPhoneInListing);

public record AvatarResponse(string AvatarUrl);

/// <summary>
/// Публичный профиль продавца. Ни email, ни телефона, ни точной даты
/// регистрации, ни Role, ни IsBanned. Дата регистрации — только месяц и год
/// (<see cref="RegisteredAt"/> — всегда первое число месяца).
/// </summary>
public record PublicProfileResponse(
    string DisplayName,
    City City,
    string? AvatarUrl,
    DateOnly RegisteredAt,
    int ActiveListingsCount,
    double? AverageRating,
    int ReviewsCount,
    bool PhoneVerified);
