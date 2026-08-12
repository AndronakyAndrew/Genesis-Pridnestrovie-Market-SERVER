using Microsoft.Extensions.Logging;
using Quartz;

namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>
/// Ежедневный джоб гигиены каталога. Тонкая обёртка над <see cref="ICatalogHygieneService"/>:
/// вся логика (архивация + предупреждения) — в сервисе, здесь только вызов.
/// <see cref="DisallowConcurrentExecutionAttribute"/> — параллельные запуски исключены
/// (в т.ч. в кластере через persistent job store). Идемпотентность обеспечивает сам сервис.
/// </summary>
[DisallowConcurrentExecution]
public sealed class CatalogHygieneJob(
    ICatalogHygieneService hygiene,
    ILogger<CatalogHygieneJob> logger) : IJob
{
    public static readonly JobKey Key = new("catalog-hygiene");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await hygiene.RunAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Джоб гигиены каталога завершился ошибкой");
            // Пробрасываем: Quartz зафиксирует сбой; misfire-политика повторит на следующем тике.
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
