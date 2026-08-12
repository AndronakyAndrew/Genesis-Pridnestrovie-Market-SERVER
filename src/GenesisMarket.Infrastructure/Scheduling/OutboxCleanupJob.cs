using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>
/// Уборщик outbox: удаляет доставленные (Done) сообщения старше срока хранения
/// (по умолчанию 30 дней). Failed НЕ трогаем — они оставлены для разбора.
/// Полностью самодостаточен (нужен только DbContext), поэтому живёт в инфраструктуре.
/// </summary>
[DisallowConcurrentExecution]
public sealed class OutboxCleanupJob(
    AppDbContext db,
    IOptions<OutboxOptions> options,
    ILogger<OutboxCleanupJob> logger) : IJob
{
    public static readonly JobKey Key = new("outbox-cleanup");

    public async Task Execute(IJobExecutionContext context)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.RetentionDays);

        var removed = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Done && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(context.CancellationToken);

        if (removed > 0)
            logger.LogInformation("Уборка outbox: удалено доставленных сообщений — {Removed}.", removed);
    }
}
