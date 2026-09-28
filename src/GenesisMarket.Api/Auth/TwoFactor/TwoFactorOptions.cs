namespace GenesisMarket.Api.Auth.TwoFactor;

/// <summary>Настройки 2FA. Секция <c>TwoFactor</c>. Ключ шифрования — <c>Security:TotpKey</c>, только из env.</summary>
public sealed class TwoFactorOptions
{
    public const string Section = "TwoFactor";

    /// <summary>Имя сервиса в приложении-аутентификаторе.</summary>
    public string Issuer { get; set; } = "Местная площадка Genesis";

    /// <summary>Сколько живёт незавершённый вход (пароль принят, ждём код), минуты.</summary>
    public int ChallengeMinutes { get; set; } = 5;

    /// <summary>Попыток ввода кода на один незавершённый вход.</summary>
    public int ChallengeMaxAttempts { get; set; } = 5;

    /// <summary>Резервных кодов в пачке.</summary>
    public int RecoveryCodeCount { get; set; } = 10;

    /// <summary>
    /// Неудачных проверок второго фактора подряд (по аккаунту, с любых адресов), после
    /// которых второй фактор временно не принимается.
    /// </summary>
    public int LockoutThreshold { get; set; } = 10;

    /// <summary>На сколько минут блокируется второй фактор после порога.</summary>
    public int LockoutMinutes { get; set; } = 15;
}

/// <summary>
/// Короткая сессия служебных ролей. Секция <c>StaffSession</c>. Обычному пользователю
/// refresh-токен даёт скользящие 30 дней; сотруднику — не дольше <see cref="AbsoluteHours"/>
/// от входа и не дольше <see cref="IdleMinutes"/> без обновления токена.
/// </summary>
public sealed class StaffSessionOptions
{
    public const string Section = "StaffSession";

    public int AbsoluteHours { get; set; } = 12;

    public int IdleMinutes { get; set; } = 60;
}
