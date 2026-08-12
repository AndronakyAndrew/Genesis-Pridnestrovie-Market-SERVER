using GenesisMarket.Infrastructure.Outbox;
using Microsoft.Extensions.Logging;
using Quartz;

namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>
/// Тик диспетчера outbox (по умолчанию раз в 10 с). Тонкая обёртка над
/// <see cref="IOutboxDispatcher"/> (реализация — в слое Api). Батч и защиту от
/// параллельной обработки одного сообщения несколькими инстансами обеспечивает
/// сама реализация (SELECT ... FOR UPDATE SKIP LOCKED). <see cref="DisallowConcurrentExecutionAttribute"/>
/// дополнительно исключает наложение тиков на одном узле.
/// </summary>
[DisallowConcurrentExecution]
public sealed class OutboxDispatchJob(
    IOutboxDispatcher dispatcher,
    ILogger<OutboxDispatchJob> logger) : IJob
{
    public static readonly JobKey Key = new("outbox-dispatch");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await dispatcher.DispatchOnceAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Штатная остановка приложения — не ошибка.
        }
        catch (Exception ex)
        {
            // Инфраструктурный сбой самого тика (напр. БД недоступна): логируем и ждём
            // следующего тика. Ошибки доставки отдельных сообщений диспетчер обрабатывает сам.
            logger.LogError(ex, "Тик диспетчера outbox завершился ошибкой");
        }
    }
}
