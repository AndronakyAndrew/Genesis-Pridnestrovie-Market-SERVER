namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Транзакционный outbox: побочные эффекты во внешних системах (удаление объектов
/// в хранилище) фиксируются в БД в одной транзакции с доменным изменением и
/// выполняются фоновым обработчиком. Так HTTP-запрос не ждёт MinIO и не падает,
/// если хранилище временно недоступно.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Тип сообщения. Пока единственный — <see cref="DeleteObject"/>.</summary>
    public required string Type { get; set; }

    /// <summary>Полезная нагрузка. Для <see cref="DeleteObject"/> — ключ объекта в хранилище.</summary>
    public required string Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Заполнена ⇒ сообщение обработано и повторно не выбирается.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Сколько раз обработчик пытался выполнить сообщение (для диагностики).</summary>
    public int Attempts { get; set; }

    /// <summary>Текст последней ошибки обработки, если была.</summary>
    public string? LastError { get; set; }

    /// <summary>Тип «удалить объект из хранилища»; <see cref="Payload"/> — ключ объекта.</summary>
    public const string DeleteObject = "delete-object";
}
