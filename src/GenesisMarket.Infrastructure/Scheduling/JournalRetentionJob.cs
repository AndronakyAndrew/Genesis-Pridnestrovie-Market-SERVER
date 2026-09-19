using Microsoft.Extensions.Logging;
using Quartz;

namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>
/// Ежедневный джоб уборки журналов с псевдонимизированным IP. Тонкая обёртка над
/// <see cref="IJournalRetentionService"/>: вся логика — в сервисе, здесь только вызов
/// (и потому уборку можно прогнать в тесте напрямую, без планировщика).
/// </summary>
[DisallowConcurrentExecution]
public sealed class JournalRetentionJob(
    IJournalRetentionService retention,
    ILogger<JournalRetentionJob> logger) : IJob
{
    public static readonly JobKey Key = new("journal-retention");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await retention.RunAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Джоб уборки журналов завершился ошибкой");
            // Пробрасываем: Quartz зафиксирует сбой; misfire-политика повторит на следующем тике.
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
