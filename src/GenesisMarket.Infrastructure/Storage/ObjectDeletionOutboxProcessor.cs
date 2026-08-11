using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GenesisMarket.Infrastructure.Storage;

/// <summary>
/// Фоновый обработчик outbox: удаляет объекты из хранилища по сообщениям
/// <see cref="OutboxMessage.DeleteObject"/>. Так удаление объектов не блокирует
/// HTTP-запрос и переживает временную недоступность MinIO (повтор на следующем тике).
/// </summary>
public sealed class ObjectDeletionOutboxProcessor(
    IServiceProvider services,
    ILogger<ObjectDeletionOutboxProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Сбой обработки outbox, повтор через интервал");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();

        var batch = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null && m.Type == OutboxMessage.DeleteObject)
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0)
            return;

        foreach (var msg in batch)
        {
            try
            {
                // RemoveObject у MinIO идемпотентен: отсутствующий объект не ошибка.
                await storage.RemoveAsync(msg.Payload, ct);
                msg.ProcessedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                msg.Attempts++;
                msg.LastError = ex.Message;
                logger.LogWarning(ex, "Не удалось удалить объект хранилища: {Key}", msg.Payload);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
