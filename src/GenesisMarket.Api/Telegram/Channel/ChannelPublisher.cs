using System.Text.Json;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Telegram.Channel;

/// <summary>Итог одного тика публикации — для логов и тестов.</summary>
public enum ChannelPublishOutcome
{
    NotConfigured,
    OutsideWindow,
    TooSoon,
    QueueEmpty,
    Published,
    Skipped,
    Retrying,
    Failed
}

/// <summary>
/// Публикация объявлений в Telegram-канал через очередь <see cref="ChannelPostQueue"/>.
/// </summary>
public interface IChannelPublisher
{
    /// <summary>
    /// Поставить одобренное объявление в очередь (Pending). В Telegram не ходит: строка
    /// добавляется в текущий DbContext и сохраняется SaveChanges вызывающего — в одной
    /// транзакции с одобрением. Если пост у объявления уже есть (вернулось в продажу или
    /// из архива), вместо новой публикации ставит в outbox возврат чистой подписи.
    /// </summary>
    Task EnqueueAsync(Guid listingId, CancellationToken ct = default);

    /// <summary>Один тик воркера: окно, интервал, самая старая Pending-запись.</summary>
    Task<ChannelPublishOutcome> PublishNextAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Подпись поста → заголовок + «✅ ПРОДАНО», кнопка снимается. Ходит в Telegram — вызывать из outbox.</summary>
    Task MarkSoldAsync(Guid listingId, CancellationToken ct = default);

    /// <summary>Подпись поста → заголовок + «⛔ Снято с публикации», кнопка снимается. Вызывать из outbox.</summary>
    Task MarkRemovedAsync(Guid listingId, CancellationToken ct = default);

    /// <summary>Вернуть посту полную подпись и кнопку (объявление снова активно). Вызывать из outbox.</summary>
    Task RestoreAsync(Guid listingId, CancellationToken ct = default);
}

public sealed class ChannelPublisher(
    AppDbContext db,
    ITelegramClient telegram,
    IOptions<ChannelPublishingOptions> options,
    IOptions<TelegramOptions> telegramOptions,
    TimeProvider time,
    ILogger<ChannelPublisher> logger) : IChannelPublisher
{
    private const int MaxErrorLength = 2048;

    /// <summary>
    /// Анонсировать можно одобренное и видимое в каталоге: Active и не в очереди модерации.
    /// Постмодерация (Active + ReviewQueuedAt) ждёт решения модератора.
    /// </summary>
    public static bool IsAnnounceable(Listing listing) =>
        listing.Status == ListingStatus.Active && listing.ReviewQueuedAt is null && listing.DeletedAt is null;

    public async Task EnqueueAsync(Guid listingId, CancellationToken ct = default)
    {
        var hasPost = await db.Listings.IgnoreQueryFilters()
            .AnyAsync(l => l.Id == listingId && l.TelegramMessageId != null, ct);

        if (hasPost)
        {
            // Пост уже висит с пометкой «Продано»/«Снято» — возвращаем подпись, а не публикуем второй раз.
            db.OutboxMessages.Add(new OutboxMessage
            {
                Type = OutboxMessage.ListingChannelUpdate,
                Payload = JsonSerializer.Serialize(new { listingId, mark = Outbox.Telegram.ChannelMark.Active })
            });
            return;
        }

        var now = time.GetUtcNow();
        var item = db.ChannelPostQueue.Local.FirstOrDefault(q => q.ListingId == listingId)
                   ?? await db.ChannelPostQueue.FirstOrDefaultAsync(q => q.ListingId == listingId, ct);

        if (item is null)
        {
            db.ChannelPostQueue.Add(new ChannelPostQueue { ListingId = listingId, EnqueuedAt = now });
            return;
        }

        // Новое одобрение даёт ещё один шанс тому, что раньше не ушло. Pending уже в очереди,
        // Published без поста (удалён вручную) не переигрываем.
        if (item.Status is ChannelPostStatus.Failed or ChannelPostStatus.Skipped)
        {
            item.Status = ChannelPostStatus.Pending;
            item.EnqueuedAt = now;
            item.PublishedAt = null;
            item.AttemptCount = 0;
            item.LastError = null;
        }
    }

    public async Task<ChannelPublishOutcome> PublishNextAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var o = options.Value;
        var channelId = telegramOptions.Value.ChannelId;
        if (string.IsNullOrWhiteSpace(channelId))
            return ChannelPublishOutcome.NotConfigured;

        var zone = ChannelSchedule.ResolveTimeZone(o.TimeZoneId)
            ?? throw new InvalidOperationException(
                $"Часовой пояс '{o.TimeZoneId}' не найден (ChannelPublishing:TimeZoneId).");

        if (!ChannelSchedule.IsWithinWindow(now, zone, o.WindowStart, o.WindowEnd))
            return ChannelPublishOutcome.OutsideWindow;

        var lastPublishedAt = await db.ChannelPostQueue.AsNoTracking()
            .Where(q => q.Status == ChannelPostStatus.Published)
            .MaxAsync(q => q.PublishedAt, ct);

        if (lastPublishedAt is { } last && now - last < TimeSpan.FromMinutes(Math.Max(0, o.MinIntervalMinutes)))
            return ChannelPublishOutcome.TooSoon;

        var item = await db.ChannelPostQueue
            .Where(q => q.Status == ChannelPostStatus.Pending)
            .OrderBy(q => q.EnqueuedAt)
            .ThenBy(q => q.Id)
            .FirstOrDefaultAsync(ct);

        if (item is null)
            return ChannelPublishOutcome.QueueEmpty;

        // Состояние — на момент публикации, а не постановки: за время ожидания в очереди
        // объявление могли продать, снять, отправить на проверку или удалить.
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == item.ListingId, ct);
        if (listing is null || !IsAnnounceable(listing))
            return await SkipAsync(item, "объявление больше не активно");
        if (listing.TelegramMessageId is not null)
            return await SkipAsync(item, "у объявления уже есть пост в канале");

        var photoKey = await db.ListingImages.AsNoTracking()
            .Where(i => i.ListingId == listing.Id)
            .OrderBy(i => i.SortOrder)
            .Select(i => i.ObjectKey)
            .FirstOrDefaultAsync(ct);
        if (photoKey is null)
            return await SkipAsync(item, "у объявления нет фото");

        var caption = ChannelPostFormatter.BuildCaption(Content(listing), o.DescriptionMaxLength);
        // Фото — URL публичной выдачи API (ImagesController), не MinIO: хранилище в приватной сети.
        var photoUrl = $"{ApiBase(o)}/api/images/{photoKey}";

        int messageId;
        try
        {
            messageId = await telegram.SendPhotoAsync(channelId, photoUrl, caption, Button(o, listing.Id), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            item.AttemptCount++;
            item.LastError = Truncate($"{ex.GetType().Name}: {ex.Message}");

            // 400 — запрос отвергнут по существу (битый URL фото, неподходящий формат, подпись):
            // повтор вернёт то же самое.
            var permanent = ex is TelegramApiException { ErrorCode: 400 };
            if (permanent || item.AttemptCount >= Math.Max(1, o.MaxAttempts))
                item.Status = ChannelPostStatus.Failed;

            await db.SaveChangesAsync(CancellationToken.None);

            logger.LogWarning(ex,
                "Публикация объявления {ListingId} в канал не удалась (попытка {Attempt}, статус {Status})",
                listing.Id, item.AttemptCount, item.Status);

            return item.Status == ChannelPostStatus.Failed
                ? ChannelPublishOutcome.Failed
                : ChannelPublishOutcome.Retrying;
        }

        item.Status = ChannelPostStatus.Published;
        item.PublishedAt = now;
        item.LastError = null;
        listing.AttachChannelPost(channelId, messageId, now);

        // Пост уже в канале: сохраняем и при остановке приложения, иначе после рестарта он уйдёт второй раз.
        await db.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation("Объявление {ListingId} опубликовано в канал, message_id {MessageId}",
            listing.Id, messageId);
        return ChannelPublishOutcome.Published;
    }

    public Task MarkSoldAsync(Guid listingId, CancellationToken ct = default) =>
        EditPostAsync(listingId, PostEdit.Sold, ct);

    public Task MarkRemovedAsync(Guid listingId, CancellationToken ct = default) =>
        EditPostAsync(listingId, PostEdit.Removed, ct);

    public Task RestoreAsync(Guid listingId, CancellationToken ct = default) =>
        EditPostAsync(listingId, PostEdit.Restore, ct);

    private enum PostEdit { Sold, Removed, Restore }

    private async Task EditPostAsync(Guid listingId, PostEdit edit, CancellationToken ct)
    {
        var listing = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listingId, ct);

        // Объявление исчезло или в канал не публиковалось — править нечего.
        if (listing is null || listing.TelegramMessageId is not { } messageId
            || string.IsNullOrEmpty(listing.TelegramChatId))
            return;

        // Между постановкой правки и доставкой объявление могли снова снять — чистую подпись не возвращаем.
        if (edit == PostEdit.Restore && !IsAnnounceable(listing))
            return;

        (string Caption, InlineButton? Button) post = edit switch
        {
            PostEdit.Sold => (ChannelPostFormatter.BuildSoldCaption(listing.Title), null),
            PostEdit.Removed => (ChannelPostFormatter.BuildRemovedCaption(listing.Title), null),
            _ => (ChannelPostFormatter.BuildCaption(Content(listing), options.Value.DescriptionMaxLength),
                Button(options.Value, listing.Id))
        };

        try
        {
            await telegram.EditCaptionAsync(listing.TelegramChatId, checked((int)messageId), post.Caption, post.Button, ct);
        }
        catch (TelegramApiException ex) when (IsNothingToEdit(ex))
        {
            logger.LogInformation("Правка поста объявления {ListingId} не нужна: {Reason}", listingId, ex.Message);
        }
    }

    private async Task<ChannelPublishOutcome> SkipAsync(ChannelPostQueue item, string reason)
    {
        item.Status = ChannelPostStatus.Skipped;
        item.LastError = reason;
        await db.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation("Публикация объявления {ListingId} в канал пропущена: {Reason}", item.ListingId, reason);
        return ChannelPublishOutcome.Skipped;
    }

    private static ChannelPostContent Content(Listing l) =>
        new(l.Title, l.Description, l.Price, l.PriceType, l.City);

    // Кнопка ведёт на редирект API, а не на сайт: /r/l живёт на API (у фронтенда такого маршрута нет),
    // и только так клик приходит с IP посетителя — через прокси Vercel у всех был бы один IP.
    private static InlineButton Button(ChannelPublishingOptions o, Guid listingId) =>
        new(ChannelPostFormatter.ButtonText, $"{ApiBase(o)}/r/l/{listingId}?s={LinkSources.Telegram}");

    private static string ApiBase(ChannelPublishingOptions o) => o.PublicApiBaseUrl.TrimEnd('/');

    // Пост удалён вручную, не изменился или больше не редактируется — это не ошибка доставки.
    private static bool IsNothingToEdit(TelegramApiException ex) =>
        ex.ErrorCode == 400 &&
        (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase)
         || ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)
         || ex.Message.Contains("message can't be edited", StringComparison.OrdinalIgnoreCase)
         || ex.Message.Contains("MESSAGE_ID_INVALID", StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string text) =>
        text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
}
