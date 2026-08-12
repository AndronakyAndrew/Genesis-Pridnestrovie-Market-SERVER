using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Outbox.Telegram;

/// <summary>
/// Настройки интеграции с Telegram Bot API. Секция <c>Telegram</c>. Секреты (токен бота,
/// идентификаторы каналов) задаются только переменными окружения / <c>.env</c>, не в коде.
/// </summary>
public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    /// <summary>Токен бота (<c>Telegram__BotToken</c>). Пусто ⇒ вместо реальной сети пишем в лог.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>
    /// Общий (fallback) канал для постов об объявлениях (<c>Telegram__BroadcastChatId</c>).
    /// Используется, когда для категории объявления нет отдельного канала. Пусто ⇒ постить некуда.
    /// </summary>
    public string BroadcastChatId { get; set; } = "";

    /// <summary>
    /// Маршрутизация «категория → канал»: словарь по значению категории в БД
    /// (<c>realestate</c>, <c>transport</c>, …) в chatId канала. Ключи регистронезависимы.
    /// Категории без записи (или с пустым значением) уходят в <see cref="BroadcastChatId"/>.
    /// Пример env: <c>Telegram__CategoryChannels__realestate=-1001234567890</c>.
    /// </summary>
    public Dictionary<string, string> CategoryChannels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Базовый публичный адрес фронтенда для ссылок в постах (<c>Telegram__WebBaseUrl</c>),
    /// например <c>https://genesis-market.pmr</c>. Пусто ⇒ в пост уйдёт относительный путь
    /// (пригодно для dev-лога, но не для реального канала).
    /// </summary>
    public string WebBaseUrl { get; set; } = "";

    /// <summary>
    /// Лимит частоты отправки в один канал: не более стольких сообщений за минуту (по правилам
    /// Telegram — 20). Соблюдается проактивно на стороне обработчика (см. <see cref="ITelegramRateLimiter"/>),
    /// а не через реакцию на 429.
    /// </summary>
    public int MaxMessagesPerMinutePerChat { get; set; } = 20;

    /// <summary>
    /// Максимум, сколько обработчик готов подождать освобождения слота лимитера, прежде чем
    /// отложить отправку (сообщение вернётся в очередь outbox и уйдёт позже). Держит транзакцию
    /// диспетчера короткой при всплесках публикаций.
    /// </summary>
    public int MaxRateLimitWaitMs { get; set; } = 5000;

    /// <summary>
    /// Разрешить канал для категории объявления: канал из <see cref="CategoryChannels"/> либо
    /// общий <see cref="BroadcastChatId"/>. Возвращает null, если ни то, ни другое не задано.
    /// </summary>
    public string? ResolveChannel(Category category)
    {
        // Сопоставляем без учёта регистра независимо от компаратора связанного словаря:
        // ключ в БД/конфиге — lowercase (realestate), имя члена enum — PascalCase (RealEstate).
        var name = category.ToString();
        foreach (var (key, chat) in CategoryChannels)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(chat))
                return chat;

        return string.IsNullOrWhiteSpace(BroadcastChatId) ? null : BroadcastChatId;
    }
}
