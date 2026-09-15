namespace GenesisMarket.Api.Telegram.Channel;

/// <summary>
/// Фоновая публикация объявлений в Telegram-канал. Секция <c>ChannelPublishing</c>.
/// Канал и токен бота — в секции <c>Telegram</c> (<see cref="TelegramOptions.ChannelId"/>,
/// <see cref="TelegramOptions.BotToken"/>), только из окружения.
/// </summary>
public sealed class ChannelPublishingOptions
{
    public const string Section = "ChannelPublishing";

    /// <summary>
    /// Выключатель воркера. По умолчанию выключен: публикация включается явно
    /// (<c>TELEGRAM_PUBLISHING_ENABLED=true</c>) после проверки выката. Очередь наполняется и без него.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Как часто воркер заглядывает в очередь, секунд.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Не чаще одного поста в столько минут. Отсчёт — от последней публикации в БД, поэтому
    /// рестарт приложения не обнуляет паузу: пачка из 100 одобренных не выльется в канал за час.
    /// </summary>
    public int MinIntervalMinutes { get; set; } = 20;

    /// <summary>Начало рабочего окна (местное время <see cref="TimeZoneId"/>), включительно.</summary>
    public TimeOnly WindowStart { get; set; } = new(9, 0);

    /// <summary>Конец рабочего окна, не включительно. Если меньше начала — окно через полночь.</summary>
    public TimeOnly WindowEnd { get; set; } = new(21, 0);

    /// <summary>Часовой пояс окна (IANA). Сервер и БД живут в UTC.</summary>
    public string TimeZoneId { get; set; } = "Europe/Chisinau";

    /// <summary>Попыток на одно объявление; затем Failed. Ответ 400 — Failed сразу.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Сколько символов описания попадает в подпись (дальше «…»).</summary>
    public int DescriptionMaxLength { get; set; } = 120;

    /// <summary>
    /// Публичный адрес API — от него строятся ссылка на фото (<c>/api/images/…</c>) и кнопка
    /// (<c>/r/l/{id}?s=tg</c>). Не адрес MinIO: хранилище в приватной сети, снаружи недоступно.
    /// </summary>
    public string PublicApiBaseUrl { get; set; } = "https://api.genesis-hq.com";
}
