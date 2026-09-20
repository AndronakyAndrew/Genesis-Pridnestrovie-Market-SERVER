namespace GenesisMarket.Api.Auth;

/// <summary>
/// Настройки JWT. Секция <c>Jwt</c>. <see cref="Key"/> берётся ТОЛЬКО из
/// переменной окружения (<c>Jwt__Key</c>) — в appsettings.json его быть не должно.
/// </summary>
public sealed class JwtOptions
{
    public const string Section = "Jwt";

    /// <summary>Ключ подписи HS256 (минимум 32 байта). Только из env.</summary>
    public string Key { get; set; } = "";

    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";

    /// <summary>Время жизни access-токена, минуты.</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Время жизни refresh-токена, дни.</summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// Окно, в котором повторное предъявление только что заменённого
    /// refresh-токена считается гонкой вкладок, а не кражей, секунды.
    /// 0 — окна нет: любой повтор отзывает цепочку.
    /// </summary>
    public int RefreshReuseGraceSeconds { get; set; } = 60;

    /// <summary>
    /// TTL кэша SecurityStamp/бана при валидации токена, секунды.
    /// 0 — не кэшировать (всегда запрос в БД); используется в тестах.
    /// </summary>
    public int SecurityStampCacheSeconds { get; set; } = 30;
}
