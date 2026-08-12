namespace GenesisMarket.Api.Outbox.Telegram;

/// <summary>
/// Telegram вернул 429 (Too Many Requests). Несёт <see cref="RetryAfter"/> из тела ответа
/// (<c>parameters.retry_after</c>) — на столько нужно отложить повтор. Ретраится Polly.
/// </summary>
public sealed class TelegramRateLimitException(TimeSpan retryAfter, string description)
    : Exception($"Telegram 429 (retry after {retryAfter.TotalSeconds:N0}s): {description}")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Временный сбой обращения к Telegram (сеть, таймаут, 5xx). Имеет смысл повторить —
/// ретраится Polly в пределах запроса, а при исчерпании — диспетчером outbox.
/// </summary>
public sealed class TelegramTransientException(string message) : Exception(message);

/// <summary>
/// Telegram отклонил запрос с 4xx (кроме 429): некорректный запрос, нет прав, чат/сообщение
/// не найдены и т.п. Повторять бессмысленно. Несёт <see cref="ErrorCode"/> и
/// <see cref="Description"/> — вызывающий решает, как реагировать (например, откатиться к
/// текстовому посту при неудаче sendPhoto или молча проигнорировать удалённое сообщение).
/// </summary>
public sealed class TelegramApiException(int errorCode, string description)
    : Exception($"Telegram API {errorCode}: {description}")
{
    public int ErrorCode { get; } = errorCode;
    public string Description { get; } = description;
}
