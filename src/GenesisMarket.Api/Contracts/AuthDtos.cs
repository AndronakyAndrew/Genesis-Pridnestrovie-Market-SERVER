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
    [Required] string City,
    [Required, MaxLength(20)] string Phone);

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

/// <summary>Запрос одноразового кода входа на почту.</summary>
public record LoginCodeRequest(
    [Required, EmailAddress, MaxLength(256)] string Email);

/// <summary>Вход по коду из письма (см. <c>POST /api/auth/login-code</c>).</summary>
public record LoginByCodeRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(12)] string Code);

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
    /// <summary>
    /// «ID профиля». Уходит вместе с сессией, чтобы клиент мог сразу сверять
    /// владельца («это моё объявление?») — иначе проверка ждала бы /api/me и
    /// интерфейс мигал бы, как это было с ролью.
    /// </summary>
    string PublicCode,
    string Email,
    string DisplayName,
    string City,
    string? PhoneE164,
    bool PhoneVerified,
    DateTimeOffset CreatedAt,
    /// <summary>
    /// Подтверждена ли почта. Уходит вместе с сессией по той же причине, что и
    /// <see cref="PublicCode"/>: от неё зависит доступность раскрытия контактов и
    /// публикации, и без неё интерфейс узнавал бы о запрете только по 403 в ответ
    /// на действие пользователя. Поле добавлено в конец: старый клиент его не читает.
    /// </summary>
    bool EmailVerified = false);

public record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserResponse User);

/// <summary>Запрос ссылки восстановления пароля. Ответ всегда одинаков (анти-перечисление).</summary>
public record ForgotPasswordRequest(
    [Required, EmailAddress, MaxLength(256)] string Email);

/// <summary>Смена пароля по одноразовому токену из письма.</summary>
public record ResetPasswordRequest(
    [Required, MaxLength(128)] string Token,
    [Required] string NewPassword);
