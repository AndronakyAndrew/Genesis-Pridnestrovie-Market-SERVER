using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Регистрация. Телефон указывается при регистрации, но подтверждается позже,
/// из профиля (см. /api/me/phone). Role игнорируется — всегда User.
/// </summary>
public record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required] string Password,
    [Required, MinLength(2), MaxLength(60)] string DisplayName,
    [Required] City City,
    [Required, MaxLength(20)] string Phone);

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public record RefreshRequest(
    [Required] string RefreshToken);

public record LogoutRequest(
    [Required] string RefreshToken);

public record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required] string NewPassword);

/// <summary>Нейтральный ответ (регистрация, отправка кода) — без утечки деталей.</summary>
public record MessageResponse(string Message);

/// <summary>
/// Данные пользователя наружу. БЕЗ PasswordHash, SecurityStamp и Role
/// (роль живёт только в токене).
/// </summary>
public record UserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    City City,
    string? PhoneE164,
    bool PhoneVerified,
    DateTimeOffset CreatedAt);

public record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserResponse User);
