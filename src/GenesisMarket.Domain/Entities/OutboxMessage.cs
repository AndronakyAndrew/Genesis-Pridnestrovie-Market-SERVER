using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Транзакционный outbox — единая точка всех внешних отправок (email, Telegram,
/// удаление объектов в хранилище). Сообщение записывается в БД в ТОЙ ЖЕ транзакции,
/// что и доменное изменение: при откате транзакции сообщения не остаётся, поэтому
/// уведомление не может «уйти» по откатанному действию. Доставку выполняет фоновый
/// диспетчер отдельно от HTTP-запроса, с ретраями и переживанием недоступности каналов.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Тип сообщения — по нему диспетчер выбирает обработчик. См. константы ниже.</summary>
    public required string Type { get; set; }

    /// <summary>
    /// Полезная нагрузка. Для уведомлений — JSON с минимумом для рендера шаблона
    /// (идентификаторы, а не персональные данные: адрес/имя обработчик достаёт из БД
    /// по id на момент отправки). Для <see cref="DeleteImages"/> — JSON-массив ключей.
    /// Для legacy <see cref="DeleteObject"/> — один ключ объекта строкой.
    /// </summary>
    public required string Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Текущий статус обработки. Обрабатываются только <see cref="OutboxStatus.Pending"/>.</summary>
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    /// <summary>
    /// Момент, начиная с которого сообщение можно (повторно) взять в обработку.
    /// Для нового сообщения — время создания; после сбоя сдвигается вперёд по
    /// экспоненциальному расписанию ретраев. Диспетчер выбирает <c>NextAttemptAt ≤ now</c>.
    /// </summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Заполнена ⇒ сообщение доставлено (<see cref="OutboxStatus.Done"/>).</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Число выполненных попыток доставки. По достижении <see cref="MaxAttempts"/> — Failed.</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Текст последней ошибки доставки (для разбора Failed-сообщений). Персональные
    /// данные (email/телефон) вычищаются перед записью — см. обработчик диспетчера.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>Максимум попыток доставки; затем <see cref="OutboxStatus.Failed"/>.</summary>
    public const int MaxAttempts = 5;

    // ---- Типы сообщений (стартовый набор). Значения — стабильные строковые ключи. ----

    /// <summary>Объявление одобрено модератором → уведомление автору. Payload: <c>{ listingId }</c>.</summary>
    public const string ListingApproved = "listing-approved";

    /// <summary>Объявление отклонено модератором → уведомление автору. Payload: <c>{ listingId, reason, comment }</c>.</summary>
    public const string ListingRejected = "listing-rejected";

    /// <summary>Скоро автоархивация → уведомление автору. Payload: <c>{ listingId, archiveAt }</c>.</summary>
    public const string ListingExpiringSoon = "listing-expiring-soon";

    /// <summary>Новый отзыв → уведомление адресату отзыва. Payload: <c>{ reviewId }</c>.</summary>
    public const string NewReview = "new-review";

    /// <summary>Объявление опубликовано → пост в Telegram-канал (шаг 16). Payload: <c>{ listingId }</c>.</summary>
    public const string ListingPublished = "listing-published";

    /// <summary>Удалить объекты из хранилища (MinIO). Payload: JSON-массив ключей.</summary>
    public const string DeleteImages = "delete-images";

    /// <summary>
    /// Legacy: удалить один объект из хранилища; <see cref="Payload"/> — ключ строкой.
    /// Оставлен для in-flight сообщений, записанных до объединения в <see cref="DeleteImages"/>.
    /// </summary>
    public const string DeleteObject = "delete-object";
}
