using System.Text.Json;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.SavedSearches;

/// <summary>
/// Прогон рассылки по сохранённым поискам. Для каждого активного поиска, готового к
/// уведомлению (прошёл час с прошлого письма), выполняет тот же запрос, что и каталог,
/// с дополнительным условием по курсору (PublishedAt, Id) &gt; последнего уведомлённого.
/// Найдено больше нуля — одно Outbox-сообщение со списком (максимум N объявлений),
/// курсор и <c>NotifiedAt</c> двигаются вперёд. Обработка идёт батчами; критерии из jsonb
/// заново валидируются перед каждым прогоном — содержимому хранилища не доверяем.
/// </summary>
public sealed class SavedSearchNotificationService(
    AppDbContext db,
    IOptions<SavedSearchOptions> options,
    ILogger<SavedSearchNotificationService> logger) : ISavedSearchNotificationService
{
    private readonly SavedSearchOptions _o = options.Value;

    public async Task<SavedSearchNotificationResult> RunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // «Не чаще одного уведомления в час»: поиски, уведомлённые позже этого порога, пропускаем.
        var notifyCutoff = now.AddMinutes(-Math.Max(1, _o.MinNotificationIntervalMinutes));
        var batchSize = Math.Max(1, _o.BatchSize);
        var cap = Math.Max(1, _o.MaxListingsPerNotification);

        var scanned = 0;
        var notified = 0;
        var afterId = Guid.Empty;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Активные поиски с включённым каналом, готовые к рассылке. Keyset по Id — батчами.
            var batch = await db.SavedSearches
                .Where(s => s.IsActive
                            && s.NotifyChannel != SavedSearchNotifyChannel.None
                            && (s.NotifiedAt == null || s.NotifiedAt < notifyCutoff)
                            && s.Id.CompareTo(afterId) > 0)
                .OrderBy(s => s.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
                break;

            foreach (var search in batch)
            {
                scanned++;
                try
                {
                    if (await ProcessAsync(search, now, cap, ct))
                        notified++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Сбой одного поиска не должен ронять весь батч — логируем и идём дальше.
                    logger.LogError(ex, "Не удалось обработать сохранённый поиск {SavedSearchId}", search.Id);
                }
            }

            // Мутации поисков и новые Outbox-сообщения — одной транзакцией на батч.
            await db.SaveChangesAsync(ct);

            afterId = batch[^1].Id;
            if (batch.Count < batchSize)
                break;
        }

        if (notified > 0)
            logger.LogInformation(
                "Рассылка сохранённых поисков: рассмотрено {Scanned}, уведомлено {Notified}.", scanned, notified);

        return new SavedSearchNotificationResult(scanned, notified);
    }

    /// <summary>Обрабатывает один поиск. Возвращает true, если ушло уведомление.</summary>
    private async Task<bool> ProcessAsync(SavedSearch search, DateTimeOffset now, int cap, CancellationToken ct)
    {
        search.LastRunAt = now;

        // Не доверяем jsonb: заново разбираем и валидируем критерии теми же правилами, что и каталог.
        if (!SavedSearchJson.TryDeserialize(search.QueryJson, out var query))
        {
            logger.LogWarning("Сохранённый поиск {SavedSearchId} деактивирован: некорректный QueryJson", search.Id);
            search.IsActive = false;
            return false;
        }

        if (CatalogQueryBuilder.ValidateFilters(query.Cities, query.PriceFrom, query.PriceTo) is { } error)
        {
            logger.LogWarning("Сохранённый поиск {SavedSearchId} деактивирован: {Error}", search.Id, error);
            search.IsActive = false;
            return false;
        }

        // Курсор (PublishedAt, Id): PublishedAt берём у последнего уведомлённого объявления.
        DateTimeOffset? cursorPublishedAt = null;
        var cursorId = Guid.Empty;
        if (search.LastNotifiedListingId is { } lastId)
        {
            var publishedAt = await db.Listings.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.Id == lastId)
                .Select(l => l.PublishedAt)
                .FirstOrDefaultAsync(ct);

            // Если объявление-якорь исчезло/не опубликовано — курсор «с начала» (редкий случай).
            if (publishedAt is { } p)
            {
                cursorPublishedAt = p;
                cursorId = lastId;
            }
        }

        var matchQuery = SavedSearchQueryPlanner.Filter(db.Listings.AsNoTracking(), query)
            .Where(l => l.PublishedAt != null);

        if (cursorPublishedAt is { } cp)
            matchQuery = CatalogQueryBuilder.ApplyPublishedAfter(matchQuery, cp, cursorId);

        // По курсору, а не по времени: строгий порядок (PublishedAt, Id) — без потерь и дублей
        // при одинаковых timestamp. Берём самые ранние неуведомлённые, не больше лимита письма.
        var matches = await matchQuery
            .OrderBy(l => l.PublishedAt).ThenBy(l => l.Id)
            .Select(l => l.Id)
            .Take(cap)
            .ToListAsync(ct);

        if (matches.Count == 0)
            return false;

        // Одно сообщение со списком; персональных данных нет — получателя и рендер соберёт обработчик.
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.SavedSearchMatch,
            Payload = JsonSerializer.Serialize(new { savedSearchId = search.Id, listingIds = matches })
        });

        // Курсор — на самое свежее из включённых (последнее в порядке возрастания).
        search.LastNotifiedListingId = matches[^1];
        search.NotifiedAt = now;
        return true;
    }
}
