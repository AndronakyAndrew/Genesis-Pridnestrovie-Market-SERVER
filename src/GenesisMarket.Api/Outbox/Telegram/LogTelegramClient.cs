using GenesisMarket.Domain.Enums;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Outbox.Telegram;

/// <summary>
/// Dev-клиент: не ходит в сеть, а пишет намерение в лог (когда токен бота не задан).
/// Возвращает синтетические message_id, чтобы остальной конвейер (сохранение id поста,
/// последующая правка «Продано»/«Снято») работал и без реального Telegram.
/// </summary>
public sealed class LogTelegramClient(IOptions<TelegramOptions> options, ILogger<LogTelegramClient> logger)
    : ITelegramClient
{
    private readonly TelegramOptions _o = options.Value;
    private long _counter;

    public string? ResolveChannel(Category category) =>
        _o.ResolveChannel(category) ?? "dev-broadcast";

    public Task<long> SendMessageAsync(string chatId, string text, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _counter);
        logger.LogWarning("[DEV TELEGRAM sendMessage] chat={ChatId} msg={MessageId}\n{Text}", chatId, id, text);
        return Task.FromResult(id);
    }

    public Task<long> SendPhotoAsync(string chatId, string photoUrl, string caption, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _counter);
        logger.LogWarning("[DEV TELEGRAM sendPhoto] chat={ChatId} msg={MessageId} photo={Photo}\n{Caption}",
            chatId, id, photoUrl, caption);
        return Task.FromResult(id);
    }

    public Task<bool> EditPostAsync(string chatId, long messageId, string text, CancellationToken ct)
    {
        logger.LogWarning("[DEV TELEGRAM editPost] chat={ChatId} msg={MessageId}\n{Text}", chatId, messageId, text);
        return Task.FromResult(true);
    }
}
