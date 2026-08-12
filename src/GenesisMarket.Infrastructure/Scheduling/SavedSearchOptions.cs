namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>
/// Настройки сохранённых поисков и их рассылки. Секция <c>SavedSearch</c> (env-override
/// <c>SavedSearch__Ключ</c>). Используется и джобом (Infrastructure), и эндпоинтами (Api,
/// лимит активных поисков).
/// </summary>
public sealed class SavedSearchOptions
{
    public const string Section = "SavedSearch";

    /// <summary>CRON запуска джоба уведомлений (Quartz-формат). По умолчанию — раз в 15 минут.</summary>
    public string NotificationCron { get; set; } = "0 0/15 * * * ?";

    /// <summary>Сколько поисков джоб обрабатывает за один проход (батч). По умолчанию 200.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>Максимум объявлений в одном письме/уведомлении. По умолчанию 10.</summary>
    public int MaxListingsPerNotification { get; set; } = 10;

    /// <summary>Не чаще одного уведомления на поиск в столько минут. По умолчанию 60 (раз в час).</summary>
    public int MinNotificationIntervalMinutes { get; set; } = 60;

    /// <summary>Лимит активных сохранённых поисков на пользователя. По умолчанию 10.</summary>
    public int MaxActivePerUser { get; set; } = 10;
}
