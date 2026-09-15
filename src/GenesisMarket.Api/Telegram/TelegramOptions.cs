using System.Text.RegularExpressions;

namespace GenesisMarket.Api.Telegram;

/// <summary>
/// Настройки служебного бота: обращения пользователей уходят админу, ответы возвращаются.
/// Секция <c>Telegram</c> общая с публикацией через outbox (<see cref="Outbox.Telegram.TelegramOptions"/>),
/// токен бота у них один. <see cref="BotToken"/>, <see cref="ChannelId"/> и <see cref="AdminChatId"/>
/// задаются только переменными окружения, в appsettings их нет.
/// </summary>
public sealed partial class TelegramOptions
{
    public const string SectionName = "Telegram";

    /// <summary>Адрес площадки, если <see cref="SiteBaseUrl"/> не задан или не является абсолютным http(s)-адресом.</summary>
    public const string DefaultSiteBaseUrl = "https://market.genesis-hq.com";

    /// <summary>Токен от @BotFather (<c>Telegram__BotToken</c>). Только через переменные окружения, не в appsettings.json.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Канал (<c>Telegram__ChannelId</c>): "@channel_name" или числовой id вида -1001234567890.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Личный chat_id админа (<c>Telegram__AdminChatId</c>) — сюда падают обращения пользователей. 0 — не задан.</summary>
    public long AdminChatId { get; set; }

    /// <summary>Базовый адрес площадки, для ссылок в постах.</summary>
    public string SiteBaseUrl { get; set; } = DefaultSiteBaseUrl;

    /// <summary>Включает long polling. На проде с несколькими инстансами держать true только на одном.</summary>
    public bool EnableUpdatePolling { get; set; } = true;

    /// <summary>
    /// Что не так с токеном, либо null, если он похож на настоящий. Не бросает: без бота площадка
    /// работает, поэтому решение «запускаться или нет» принимает вызывающий — воркер пишет warning
    /// и завершается, smoke-эндпоинт отвечает 503. Сам токен в текст не попадает.
    /// </summary>
    public string? BotTokenProblem()
    {
        if (string.IsNullOrWhiteSpace(BotToken))
            return "Telegram:BotToken не задан (переменная TELEGRAM_BOT_TOKEN).";

        // Формат @BotFather: <числовой id бота>:<ключ>. Пробел или \r из .env с CRLF сюда не пройдут.
        if (!BotTokenFormat().IsMatch(BotToken))
            return "Telegram:BotToken не похож на токен @BotFather (ожидается <id>:<ключ> без пробелов).";

        return null;
    }

    [GeneratedRegex(@"^[0-9]+:[A-Za-z0-9_-]{30,}$")]
    private static partial Regex BotTokenFormat();
}
