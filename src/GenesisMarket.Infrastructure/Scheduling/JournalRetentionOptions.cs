namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>
/// Срок хранения псевдонимизированных данных в журналах. Секция <c>JournalRetention</c>.
///
/// IpHash — не анонимизация, а псевдонимизация: значение детерминировано, и при утечке
/// ключа (<c>Security:IpHashKey</c>) весь диапазон IPv4 перебирается за минуты. Поэтому
/// держать его бессрочно незачем: анти-скрейпингу нужен последний час, всё остальное —
/// накопленный риск без применения.
/// </summary>
public sealed class JournalRetentionOptions
{
    public const string Section = "JournalRetention";

    /// <summary>Через сколько дней снимать IpHash с раскрытий контактов.</summary>
    public int ContactRevealIpDays { get; set; } = 90;

    /// <summary>Через сколько дней удалять переходы по внешним ссылкам целиком.</summary>
    public int LinkClickDays { get; set; } = 90;

    /// <summary>CRON запуска (Quartz-формат). По умолчанию — ежедневно в 03:30 UTC.</summary>
    public string CleanupCron { get; set; } = "0 30 3 * * ?";
}
