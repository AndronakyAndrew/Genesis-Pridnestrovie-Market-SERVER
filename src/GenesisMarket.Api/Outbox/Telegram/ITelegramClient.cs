using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Outbox.Telegram;

/// <summary>
/// Клиент Telegram Bot API в объёме, нужном для публикации объявлений в каналы и личных
/// уведомлений. Весь пользовательский текст отправляется как plain text (без parse_mode):
/// заголовок и описание пишет пользователь, разметку экранировать ненадёжно — проще не
/// использовать её вовсе. Соблюдение лимита частоты и ретраи 429 — внутри реализации.
/// </summary>
public interface ITelegramClient
{
    /// <summary>
    /// Канал для публикации объявления данной категории (канал категории или общий),
    /// либо null, если публичные каналы не сконфигурированы.
    /// </summary>
    string? ResolveChannel(Category category);

    /// <summary>Пост с изображением (sendPhoto). Возвращает message_id созданного поста.</summary>
    Task<long> SendPhotoAsync(string chatId, string photoUrl, string caption, CancellationToken ct);

    /// <summary>Текстовый пост/сообщение (sendMessage). Возвращает message_id.</summary>
    Task<long> SendMessageAsync(string chatId, string text, CancellationToken ct);

    /// <summary>
    /// Отредактировать ранее опубликованный пост: сначала как подпись к фото
    /// (editMessageCaption), при отсутствии подписи — как текст (editMessageText).
    /// Возвращает false, если сообщение уже удалено/недоступно для правки (не ошибка —
    /// пост могли снять вручную).
    /// </summary>
    Task<bool> EditPostAsync(string chatId, long messageId, string text, CancellationToken ct);
}
