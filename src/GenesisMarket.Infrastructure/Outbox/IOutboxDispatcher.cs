namespace GenesisMarket.Infrastructure.Outbox;

/// <summary>Итог одного прогона диспетчера outbox (для логов и тестов).</summary>
/// <param name="Claimed">Сколько сообщений взято в обработку в этом тике.</param>
/// <param name="Delivered">Сколько доставлено успешно.</param>
/// <param name="Retrying">Сколько отложено на повторную попытку.</param>
/// <param name="Failed">Сколько переведено в терминальный Failed (исчерпаны попытки).</param>
public readonly record struct OutboxDispatchResult(int Claimed, int Delivered, int Retrying, int Failed);

/// <summary>
/// Диспетчер транзакционного outbox. Реализация живёт в слое Api (там каналы доставки:
/// email/Telegram и рендер шаблонов), интерфейс — здесь, чтобы Quartz-джоб из инфраструктуры
/// мог её вызвать, а тесты — прогнать напрямую (планировщик в тестах выключен).
/// </summary>
public interface IOutboxDispatcher
{
    /// <summary>Обработать один батч готовых к отправке сообщений.</summary>
    Task<OutboxDispatchResult> DispatchOnceAsync(CancellationToken ct);
}
