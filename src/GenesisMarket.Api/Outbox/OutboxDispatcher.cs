using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Outbox;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Outbox;

/// <summary>
/// Диспетчер транзакционного outbox. За один тик забирает батч готовых сообщений,
/// каждое отдаёт обработчику по типу и фиксирует исход.
///
/// <para><b>Конкурентность.</b> Батч выбирается <c>SELECT ... FOR UPDATE SKIP LOCKED</c> внутри
/// одной транзакции: параллельные инстансы/тики не возьмут одни и те же строки, а обработка
/// и запись исхода происходят до COMMIT. При таком одно-транзакционном варианте статус
/// <see cref="OutboxStatus.Processing"/> — транзиентный (виден только внутри транзакции);
/// он есть в схеме для перехода на двухфазную обработку при горизонтальном масштабировании.</para>
///
/// <para><b>Ретраи.</b> Задержки персистентны (через <c>NextAttemptAt</c>), а не in-memory:
/// это переживает рестарт и не держит воркер занятым часами между попытками. Расписание
/// экспоненциальное: 10с, 1м, 5м, 30м, 2ч; максимум <see cref="OutboxMessage.MaxAttempts"/>
/// попыток, затем <see cref="OutboxStatus.Failed"/> (сообщение сохраняется для разбора).</para>
/// </summary>
public sealed class OutboxDispatcher(
    AppDbContext db,
    IEnumerable<IOutboxHandler> handlers,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : IOutboxDispatcher
{
    /// <summary>
    /// Экспоненциальное расписание пауз между попытками. Применяется <c>RetryDelays[Attempts-1]</c>
    /// (с фиксацией на последнем элементе). При <see cref="OutboxMessage.MaxAttempts"/> = 5
    /// реально используются первые четыре; последняя (2ч) — запас при увеличении лимита.
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2)
    ];

    private readonly Dictionary<string, IOutboxHandler> _handlers =
        handlers.ToDictionary(h => h.Type, StringComparer.Ordinal);

    private readonly OutboxOptions _o = options.Value;

    public async Task<OutboxDispatchResult> DispatchOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var batchSize = Math.Max(1, _o.BatchSize);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Забираем готовые к отправке (Pending, время попытки подошло) и блокируем строки:
        // SKIP LOCKED пропускает те, что уже держит другой инстанс/тик — без ожидания.
        var batch = await db.OutboxMessages
            .FromSqlRaw(
                """
                SELECT * FROM outbox_messages
                WHERE "Status" = 'Pending' AND "NextAttemptAt" <= {0}
                ORDER BY "CreatedAt"
                LIMIT {1}
                FOR UPDATE SKIP LOCKED
                """, now, batchSize)
            .ToListAsync(ct);

        if (batch.Count == 0)
        {
            await tx.CommitAsync(ct);
            return default;
        }

        var delivered = 0;
        var retrying = 0;
        var failed = 0;

        foreach (var message in batch)
        {
            message.Attempts++;

            try
            {
                if (!_handlers.TryGetValue(message.Type, out var handler))
                    throw new OutboxPermanentException($"Нет обработчика для типа '{message.Type}'.");

                await handler.HandleAsync(message, ct);

                message.Status = OutboxStatus.Done;
                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.Error = null;
                delivered++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Остановка приложения: не считаем попыткой, откатываем весь батч.
                message.Attempts--;
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
            catch (OutboxPermanentException ex)
            {
                message.Status = OutboxStatus.Failed;
                message.Error = PiiScrubber.Scrub(ex.Message);
                failed++;
                logger.LogError(
                    "Сообщение outbox {Id} ({Type}) отклонено без ретрая: {Reason}",
                    message.Id, message.Type, message.Error);
            }
            catch (Exception ex)
            {
                message.Error = PiiScrubber.Scrub($"{ex.GetType().Name}: {ex.Message}");

                if (message.Attempts >= OutboxMessage.MaxAttempts)
                {
                    message.Status = OutboxStatus.Failed;
                    failed++;
                    logger.LogError(
                        "Сообщение outbox {Id} ({Type}) исчерпало попытки ({Attempts}) и переведено в Failed: {Reason}",
                        message.Id, message.Type, message.Attempts, message.Error);
                }
                else
                {
                    var delay = RetryDelays[Math.Min(message.Attempts - 1, RetryDelays.Length - 1)];
                    message.NextAttemptAt = DateTimeOffset.UtcNow + delay;
                    retrying++;
                    logger.LogWarning(
                        "Сообщение outbox {Id} ({Type}), попытка {Attempts} неуспешна, повтор через {Delay}: {Reason}",
                        message.Id, message.Type, message.Attempts, delay, message.Error);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new OutboxDispatchResult(batch.Count, delivered, retrying, failed);
    }
}
