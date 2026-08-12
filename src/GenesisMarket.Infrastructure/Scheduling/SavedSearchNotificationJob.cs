using Microsoft.Extensions.Logging;
using Quartz;

namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>
/// Джоб рассылки по сохранённым поискам (по умолчанию раз в 15 минут). Тонкая обёртка над
/// <see cref="ISavedSearchNotificationService"/> (реализация — в слое Api). Батчи, курсор,
/// «не чаще раза в час» и идемпотентность обеспечивает сам сервис.
/// <see cref="DisallowConcurrentExecutionAttribute"/> исключает наложение прогонов на одном
/// узле; persistent store с кластеризацией — на разных.
/// </summary>
[DisallowConcurrentExecution]
public sealed class SavedSearchNotificationJob(
    ISavedSearchNotificationService service,
    ILogger<SavedSearchNotificationJob> logger) : IJob
{
    public static readonly JobKey Key = new("saved-search-notification");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await service.RunAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Штатная остановка приложения — не ошибка.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Джоб рассылки сохранённых поисков завершился ошибкой");
            // Пробрасываем: Quartz зафиксирует сбой; misfire-политика повторит на следующем тике.
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
