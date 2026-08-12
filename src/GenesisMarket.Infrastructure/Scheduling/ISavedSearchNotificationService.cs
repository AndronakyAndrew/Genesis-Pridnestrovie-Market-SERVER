namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>Итог одного прогона рассылки сохранённых поисков (для логов и тестов).</summary>
/// <param name="Scanned">Сколько поисков рассмотрено в этом прогоне.</param>
/// <param name="Notified">По скольким поискам ушло уведомление (нашлись новые объявления).</param>
public readonly record struct SavedSearchNotificationResult(int Scanned, int Notified);

/// <summary>
/// Рассылка по сохранённым поискам. Реализация живёт в слое Api (там билдер запроса каталога
/// и его валидаторы), интерфейс — здесь, чтобы Quartz-джоб из инфраструктуры мог её вызвать,
/// а тесты — прогнать напрямую (планировщик в тестах выключен).
/// </summary>
public interface ISavedSearchNotificationService
{
    /// <summary>Обработать все готовые к рассылке сохранённые поиски (батчами).</summary>
    Task<SavedSearchNotificationResult> RunAsync(CancellationToken ct);
}
