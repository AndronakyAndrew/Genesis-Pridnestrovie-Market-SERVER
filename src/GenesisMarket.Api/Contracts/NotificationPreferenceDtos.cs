using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

public record NotificationPreferenceDto(
    string Key,
    string Description,
    bool Enabled,
    bool CanDisable,
    IReadOnlyList<string> Types);

public record NotificationPreferencesResponse(
    IReadOnlyList<NotificationPreferenceDto> Groups,
    NotificationChannel NotifyVia);

public record UpdateNotificationPreferencesRequest(
    Dictionary<string, bool>? Groups,
    NotificationChannel? NotifyVia);

public record EmailChangeSendCodeRequest(
    [Required, MaxLength(254)] string NewEmail);

public record EmailChangeVerifyRequest(
    [Required, MaxLength(254)] string NewEmail,
    [Required, MinLength(1), MaxLength(32)] string Code);

public record EmailChangeResponse(
    string Email,
    bool EmailVerified,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt);

public record AccountDeleteSendCodeResponse(string Message, DateTimeOffset ExpiresAt);

public record AccountDeleteConfirmRequest(
    [Required, MinLength(1), MaxLength(32)] string Code,
    [MaxLength(32)] string? TwoFactorCode);
