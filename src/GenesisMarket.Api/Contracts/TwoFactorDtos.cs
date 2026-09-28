using System.ComponentModel.DataAnnotations;

namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Ответ входа при включённой 2FA (HTTP 202): токенов ещё нет, есть билет на ввод
/// второго фактора. Отдельный статус, а не поле в <see cref="AuthResponse"/>: клиент,
/// не знающий про 2FA, не примет билет за сессию.
/// </summary>
public record MfaChallengeResponse(bool MfaRequired, string ChallengeToken, DateTimeOffset ExpiresAt);

/// <summary>Второй шаг входа: код из приложения или резервный код.</summary>
public record MfaVerifyRequest(
    [Required, MaxLength(128)] string ChallengeToken,
    [Required, MaxLength(32)] string Code);

/// <summary>
/// Состояние 2FA своего аккаунта. <paramref name="Required"/> — роль служебная и без 2FA
/// служебный раздел закрыт.
/// </summary>
public record TwoFactorStatusResponse(bool Enabled, bool SetupPending, bool Required, int RecoveryCodesLeft);

/// <summary>
/// Начало настройки: секрет для ручного ввода и otpauth-ссылка для QR (QR рисует
/// клиент). Показывается один раз — повторный вызов выдаёт новый секрет.
/// </summary>
public record TwoFactorSetupResponse(string Secret, string ProvisioningUri);

public record TwoFactorCodeRequest([Required, MaxLength(32)] string Code);

/// <summary>
/// 2FA включена. Резервные коды показываются один раз. Токены — новая пара с claim
/// mfa для этой же сессии, чтобы служебный раздел открылся без повторного входа.
/// </summary>
public record TwoFactorEnabledResponse(
    IReadOnlyList<string> RecoveryCodes,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt);

public record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);
