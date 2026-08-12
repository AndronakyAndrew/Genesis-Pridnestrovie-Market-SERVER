using GenesisMarket.Domain.Entities;

namespace GenesisMarket.Api.Outbox;

/// <summary>
/// Обработчик одного типа сообщений outbox. Диспетчер маршрутизирует сообщение по
/// <see cref="Type"/>. Исключение из <see cref="HandleAsync"/> означает временный сбой
/// (будет ретрай); <see cref="OutboxPermanentException"/> — что повторять бессмысленно.
/// </summary>
public interface IOutboxHandler
{
    /// <summary>Тип сообщения (см. константы <see cref="OutboxMessage"/>), который обслуживает обработчик.</summary>
    string Type { get; }

    Task HandleAsync(OutboxMessage message, CancellationToken ct);
}
