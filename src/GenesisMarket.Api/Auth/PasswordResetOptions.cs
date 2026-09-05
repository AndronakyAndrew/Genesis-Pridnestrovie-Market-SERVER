namespace GenesisMarket.Api.Auth;

/// <summary>
/// Настройки восстановления пароля по ссылке из письма. Секция <c>PasswordReset</c>.
/// TTL короткий намеренно: ссылка даёт полный доступ к аккаунту.
/// </summary>
public sealed class PasswordResetOptions
{
    public const string Section = "PasswordReset";

    /// <summary>Сколько минут живёт ссылка из письма.</summary>
    public int TokenTtlMinutes { get; set; } = 30;

    /// <summary>Кулдаун на повторную отправку письма одному пользователю, секунды.</summary>
    public int ResendCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Путь на фронтенде, куда ведёт ссылка. Токен добавляется как <c>?token=</c>.
    /// Абсолютный адрес собирается от <c>Seo:WebBaseUrl</c>.
    /// </summary>
    public string ResetPath { get; set; } = "/reset-password";
}
