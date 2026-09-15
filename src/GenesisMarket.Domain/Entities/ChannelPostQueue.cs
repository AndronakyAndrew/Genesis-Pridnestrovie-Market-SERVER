using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Очередь публикации объявлений в Telegram-канал. Одна строка на объявление
/// (уникальный индекс по <see cref="ListingId"/>): объявление публикуется в канал один раз,
/// повторная постановка в очередь упирается в констрейнт БД, а не в проверку в коде.
/// </summary>
public class ChannelPostQueue
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Публикуемое объявление.</summary>
    public Guid ListingId { get; set; }
    public Listing? Listing { get; set; }

    /// <summary>Текущий статус. В выборку очереди попадают только <see cref="ChannelPostStatus.Pending"/>.</summary>
    public ChannelPostStatus Status { get; set; } = ChannelPostStatus.Pending;

    /// <summary>Момент постановки в очередь (UTC). По нему — FIFO выборки.</summary>
    public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Заполнена ⇒ пост отправлен (<see cref="ChannelPostStatus.Published"/>).</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Число выполненных попыток публикации.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Текст последней ошибки публикации (для разбора Failed).</summary>
    public string? LastError { get; set; }
}
