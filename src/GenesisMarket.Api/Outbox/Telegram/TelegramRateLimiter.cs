using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Outbox.Telegram;

/// <summary>
/// Проактивное ограничение частоты отправки в один чат/канал. Telegram допускает не более
/// ~20 сообщений в минуту в один канал; лимит соблюдаем ДО отправки (а не реагируя на 429),
/// чтобы не упираться в блокировку API. Реализация — скользящее окно на минуту по каждому
/// chatId.
/// </summary>
public interface ITelegramRateLimiter
{
    /// <summary>
    /// Резервирует слот на отправку в <paramref name="chatId"/>. Если окно заполнено,
    /// ждёт освобождения, но не дольше настроенного максимума; при превышении — бросает
    /// <see cref="TelegramRateLimitedLocallyException"/>, чтобы обработчик отложил отправку
    /// через outbox, а не держал транзакцию диспетчера.
    /// </summary>
    Task AcquireAsync(string chatId, CancellationToken ct);
}

/// <summary>
/// Локальный лимитер не смог выделить слот в отведённое время — отправку следует отложить
/// (сообщение вернётся в очередь outbox). Транзиентно: диспетчер повторит позже.
/// </summary>
public sealed class TelegramRateLimitedLocallyException(string chatId)
    : Exception($"Локальный лимит частоты Telegram для чата {chatId} исчерпан — отправка отложена.");

/// <summary>Скользящее окно (1 минута) на каждый chatId. Потокобезопасно; singleton.</summary>
public sealed class SlidingWindowTelegramRateLimiter(IOptions<TelegramOptions> options) : ITelegramRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TelegramOptions _o = options.Value;

    // Времена недавних отправок по каждому чату. Доступ сериализуется через lock на самой очереди.
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();

    public async Task AcquireAsync(string chatId, CancellationToken ct)
    {
        var limit = Math.Max(1, _o.MaxMessagesPerMinutePerChat);
        var maxWait = TimeSpan.FromMilliseconds(Math.Max(0, _o.MaxRateLimitWaitMs));
        var queue = _hits.GetOrAdd(chatId, _ => new Queue<DateTimeOffset>());

        var waited = TimeSpan.Zero;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan wait;
            lock (queue)
            {
                var now = DateTimeOffset.UtcNow;
                // Выкидываем отметки старше окна.
                while (queue.Count > 0 && now - queue.Peek() >= Window)
                    queue.Dequeue();

                if (queue.Count < limit)
                {
                    queue.Enqueue(now);
                    return;
                }

                // Слот освободится, когда самая ранняя отметка выйдет за окно.
                wait = queue.Peek() + Window - now;
            }

            if (wait <= TimeSpan.Zero)
                continue; // граница окна — перепроверяем без ожидания

            if (waited + wait > maxWait)
                throw new TelegramRateLimitedLocallyException(chatId);

            await Task.Delay(wait, ct);
            waited += wait;
        }
    }
}
