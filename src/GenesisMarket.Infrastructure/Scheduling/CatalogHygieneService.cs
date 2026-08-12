using System.Text.Json;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Infrastructure.Scheduling;

/// <summary>Итог одного прогона гигиены каталога (для логов и тестов).</summary>
public readonly record struct HygieneResult(int Archived, int Warned);

/// <summary>
/// Гигиена каталога: автоархивация просроченных объявлений и предупреждение авторов
/// за N дней до архивации. Вынесено из Quartz-джоба отдельным сервисом, чтобы логику
/// можно было прогонять и тестировать напрямую. Все переходы — через доменные методы
/// <see cref="Listing"/>; обработка идёт батчами и идемпотентна (повторный прогон на тех
/// же данных ничего не меняет: архивные уже не Active, предупреждённые помечены).
/// </summary>
public interface ICatalogHygieneService
{
    Task<HygieneResult> RunAsync(CancellationToken ct);
}

public sealed class CatalogHygieneService(
    AppDbContext db,
    IOptions<CatalogHygieneOptions> options,
    ILogger<CatalogHygieneService> logger) : ICatalogHygieneService
{
    private readonly CatalogHygieneOptions _o = options.Value;

    public async Task<HygieneResult> RunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // «Возраст» объявления считаем от последнего поднятия, с откатом на публикацию/создание.
        var archiveCutoff = now.AddDays(-_o.ArchiveAfterDays);
        var warnCutoff = now.AddDays(-(_o.ArchiveAfterDays - _o.WarnBeforeDays));

        var archived = await ArchiveExpiredAsync(archiveCutoff, now, ct);
        var warned = await WarnUpcomingAsync(archiveCutoff, warnCutoff, now, ct);

        if (archived > 0 || warned > 0)
            logger.LogInformation(
                "Гигиена каталога: архивировано {Archived}, предупреждено {Warned}.", archived, warned);

        return new HygieneResult(archived, warned);
    }

    /// <summary>Active с coalesce(BumpedAt, PublishedAt, CreatedAt) старше порога → Archived. Батчами.</summary>
    private async Task<int> ArchiveExpiredAsync(DateTimeOffset archiveCutoff, DateTimeOffset now, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await db.Listings
                .Where(l => l.Status == ListingStatus.Active &&
                            (l.BumpedAt ?? l.PublishedAt ?? l.CreatedAt) < archiveCutoff)
                .OrderBy(l => l.Id)
                .Take(_o.BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
                break;

            foreach (var listing in batch)
            {
                listing.Archive(now);

                // Опубликованный в канал пост помечаем «Снято с публикации» (в той же транзакции).
                // mark совпадает с константой ChannelMark.Archived на стороне Api-обработчика.
                if (listing.TelegramMessageId is not null)
                    db.OutboxMessages.Add(new OutboxMessage
                    {
                        Type = OutboxMessage.ListingChannelUpdate,
                        Payload = JsonSerializer.Serialize(new { listingId = listing.Id, mark = "archived" })
                    });
            }

            await db.SaveChangesAsync(ct);
            total += batch.Count;

            if (batch.Count < _o.BatchSize)
                break;
        }

        return total;
    }

    /// <summary>
    /// Active, ещё не предупреждённые, у которых до архивации осталось ≤ WarnBeforeDays
    /// (но срок ещё не наступил): кладём уведомление в outbox и помечаем. Батчами.
    /// </summary>
    private async Task<int> WarnUpcomingAsync(
        DateTimeOffset archiveCutoff, DateTimeOffset warnCutoff, DateTimeOffset now, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await db.Listings
                .Where(l => l.Status == ListingStatus.Active &&
                            l.ArchiveWarningAt == null &&
                            (l.BumpedAt ?? l.PublishedAt ?? l.CreatedAt) < warnCutoff &&
                            (l.BumpedAt ?? l.PublishedAt ?? l.CreatedAt) >= archiveCutoff)
                .OrderBy(l => l.Id)
                .Take(_o.BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
                break;

            foreach (var listing in batch)
            {
                var basis = listing.BumpedAt ?? listing.PublishedAt ?? listing.CreatedAt;
                var archiveAt = basis.AddDays(_o.ArchiveAfterDays);

                // Уведомление автора — через outbox в той же транзакции (доставит диспетчер).
                // В payload только идентификаторы: адресата и текст обработчик соберёт из БД.
                db.OutboxMessages.Add(new OutboxMessage
                {
                    Type = OutboxMessage.ListingExpiringSoon,
                    Payload = JsonSerializer.Serialize(new { listingId = listing.Id, archiveAt })
                });

                listing.MarkArchiveWarned(now);
            }

            await db.SaveChangesAsync(ct);
            total += batch.Count;

            if (batch.Count < _o.BatchSize)
                break;
        }

        return total;
    }
}
