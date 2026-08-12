using System.Text.Json;
using GenesisMarket.Api.Outbox.Telegram;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Outbox;

/// <summary>Разбор payload с единым сообщением об ошибке (некорректный JSON — не ретраить).</summary>
internal static class OutboxPayload
{
    // Продюсеры пишут ключи camelCase (listingId), контракты — PascalCase: сопоставляем без учёта регистра.
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static T Parse<T>(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Options)
                   ?? throw new OutboxPermanentException("Пустой payload сообщения outbox.");
        }
        catch (JsonException ex)
        {
            throw new OutboxPermanentException($"Некорректный payload: {ex.Message}");
        }
    }
}

/// <summary>Объявление одобрено → письмо/Telegram автору.</summary>
public sealed class ListingApprovedHandler(AppDbContext db, IUserNotifier notifier) : IOutboxHandler
{
    public string Type => OutboxMessage.ListingApproved;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<ListingApprovedPayload>(message.Payload);
        var listing = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == p.ListingId)
            .Select(l => new { l.OwnerId, l.Title })
            .FirstOrDefaultAsync(ct)
            ?? throw new OutboxPermanentException("Объявление не найдено.");

        await notifier.NotifyAsync(listing.OwnerId,
            "Объявление одобрено",
            $"Ваше объявление «{listing.Title}» одобрено и опубликовано.", ct);
    }
}

/// <summary>Объявление отклонено → письмо/Telegram автору с причиной.</summary>
public sealed class ListingRejectedHandler(AppDbContext db, IUserNotifier notifier) : IOutboxHandler
{
    public string Type => OutboxMessage.ListingRejected;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<ListingRejectedPayload>(message.Payload);
        var listing = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == p.ListingId)
            .Select(l => new { l.OwnerId, l.Title })
            .FirstOrDefaultAsync(ct)
            ?? throw new OutboxPermanentException("Объявление не найдено.");

        var body = $"Ваше объявление «{listing.Title}» отклонено модератором. Причина: {ReasonText(p.Reason)}.";
        if (!string.IsNullOrWhiteSpace(p.Comment))
            body += $"\nКомментарий модератора: {p.Comment}";

        await notifier.NotifyAsync(listing.OwnerId, "Объявление отклонено", body, ct);
    }

    private static string ReasonText(string reason) =>
        Enum.TryParse<ReportReason>(reason, out var r) ? r switch
        {
            ReportReason.Spam => "спам",
            ReportReason.Fraud => "мошенничество",
            ReportReason.Prohibited => "запрещённый товар или услуга",
            ReportReason.WrongCategory => "неверная категория",
            ReportReason.Duplicate => "дубликат объявления",
            ReportReason.PriceViolation => "нарушение в цене",
            _ => "нарушение правил"
        } : "нарушение правил";
}

/// <summary>Скоро автоархивация → напоминание автору поднять объявление.</summary>
public sealed class ListingExpiringSoonHandler(AppDbContext db, IUserNotifier notifier) : IOutboxHandler
{
    public string Type => OutboxMessage.ListingExpiringSoon;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<ListingExpiringSoonPayload>(message.Payload);
        var listing = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == p.ListingId)
            .Select(l => new { l.OwnerId, l.Title })
            .FirstOrDefaultAsync(ct)
            ?? throw new OutboxPermanentException("Объявление не найдено.");

        await notifier.NotifyAsync(listing.OwnerId,
            "Объявление скоро уйдёт в архив",
            $"Объявление «{listing.Title}» будет автоматически архивировано {p.ArchiveAt:dd.MM.yyyy}. " +
            "Поднимите его, чтобы продлить публикацию.", ct);
    }
}

/// <summary>Новый отзыв → уведомление адресату отзыва.</summary>
public sealed class NewReviewHandler(AppDbContext db, IUserNotifier notifier) : IOutboxHandler
{
    public string Type => OutboxMessage.NewReview;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<NewReviewPayload>(message.Payload);
        var review = await db.Reviews.AsNoTracking()
            .Where(r => r.Id == p.ReviewId && !r.IsHidden)
            .Select(r => new { r.TargetUserId, r.Rating, Title = r.Listing!.Title })
            .FirstOrDefaultAsync(ct)
            ?? throw new OutboxPermanentException("Отзыв не найден или скрыт.");

        await notifier.NotifyAsync(review.TargetUserId,
            "Новый отзыв о вас",
            $"Вам оставили новый отзыв ({review.Rating}★) по объявлению «{review.Title}». " +
            "Загляните в профиль, чтобы посмотреть.", ct);
    }
}

/// <summary>
/// Объявление опубликовано (перешло в Active) → пост в публичный Telegram-канал (шаг 16).
/// Канал выбирается по категории (с откатом на общий). Идемпотентно: если пост по этому
/// объявлению уже есть, значит объявление вернулось в продажу/из архива — правим подпись на
/// «чистую» (снимаем пометки «Продано»/«Снято») вместо повторной публикации. Первое фото —
/// через sendPhoto, при его отсутствии/сбое — sendMessage. Координаты поста сохраняем в
/// объявлении для последующих правок.
/// </summary>
public sealed class ListingPublishedHandler(
    AppDbContext db,
    ITelegramClient telegram,
    IObjectStorage storage,
    IOptions<Telegram.TelegramOptions> options) : IOutboxHandler
{
    // Пресайн-ссылка на фото должна прожить возможные ретраи отправки.
    private static readonly TimeSpan PhotoUrlTtl = TimeSpan.FromHours(1);

    public string Type => OutboxMessage.ListingPublished;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<ListingPublishedPayload>(message.Payload);

        // Трекаем сущность: обработчик пишет в неё координаты поста, диспетчер сохранит в общей транзакции.
        var listing = await db.Listings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.Id == p.ListingId, ct)
            ?? throw new OutboxPermanentException("Объявление не найдено.");

        var chat = telegram.ResolveChannel(listing.Category)
            ?? throw new OutboxPermanentException(
                "Публичный Telegram-канал не сконфигурирован (Telegram:BroadcastChatId / CategoryChannels).");

        var url = TelegramPostFormatter.BuildUrl(options.Value.WebBaseUrl, listing.Slug);
        var post = TelegramPostFormatter.BuildPost(
            listing.Title, listing.Price, listing.PriceType, listing.City, listing.Category, url);

        // Пост уже существует ⇒ это повторная активация (Sold/Archived → Active): чистим подпись.
        if (listing.TelegramMessageId is { } existingId && listing.TelegramChatId is { } existingChat)
        {
            await telegram.EditPostAsync(existingChat, existingId, post, ct);
            return;
        }

        // Первое изображение (по порядку) — постим с фото; иначе текстом.
        var photoKey = await db.ListingImages.AsNoTracking()
            .Where(i => i.ListingId == listing.Id)
            .OrderBy(i => i.SortOrder)
            .Select(i => i.ObjectKey)
            .FirstOrDefaultAsync(ct);

        long messageId;
        if (photoKey is not null)
        {
            var photoUrl = await storage.GetPresignedUrlAsync(photoKey, PhotoUrlTtl, ct);
            try
            {
                messageId = await telegram.SendPhotoAsync(chat, photoUrl, post, ct);
            }
            catch (TelegramApiException)
            {
                // Telegram не смог обработать картинку (формат/размер/URL) — не теряем анонс,
                // публикуем текстом.
                messageId = await telegram.SendMessageAsync(chat, post, ct);
            }
        }
        else
        {
            messageId = await telegram.SendMessageAsync(chat, post, ct);
        }

        listing.AttachChannelPost(chat, messageId);
    }
}

/// <summary>
/// Правка ранее опубликованного поста: пометка «Продано» (mark-sold) или «Снято с публикации»
/// (архивация/снятие). Если объявление в канал не постили или пост удалён вручную — обработчик
/// молча завершается (не ошибка).
/// </summary>
public sealed class ListingChannelUpdateHandler(
    AppDbContext db,
    ITelegramClient telegram,
    IOptions<Telegram.TelegramOptions> options) : IOutboxHandler
{
    public string Type => OutboxMessage.ListingChannelUpdate;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<ListingChannelUpdatePayload>(message.Payload);

        var listing = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == p.ListingId)
            .Select(l => new
            {
                l.Title, l.Price, l.PriceType, l.City, l.Category, l.Slug,
                l.TelegramChatId, l.TelegramMessageId
            })
            .FirstOrDefaultAsync(ct);

        // Объявление исчезло или в канал не публиковалось — править нечего.
        if (listing is null || listing.TelegramMessageId is not { } messageId
            || string.IsNullOrEmpty(listing.TelegramChatId))
            return;

        var url = TelegramPostFormatter.BuildUrl(options.Value.WebBaseUrl, listing.Slug);
        var post = TelegramPostFormatter.BuildPost(
            listing.Title, listing.Price, listing.PriceType, listing.City, listing.Category, url);

        await telegram.EditPostAsync(listing.TelegramChatId, messageId, TelegramPostFormatter.WithMark(post, p.Mark), ct);
    }
}

/// <summary>
/// Новые объявления по сохранённому поиску → одно уведомление автору со списком (до 10).
/// Канал берётся из настройки самого поиска (может отличаться от профильного).
/// </summary>
public sealed class SavedSearchMatchHandler(AppDbContext db, IUserNotifier notifier) : IOutboxHandler
{
    public string Type => OutboxMessage.SavedSearchMatch;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<SavedSearchMatchPayload>(message.Payload);

        var search = await db.SavedSearches.AsNoTracking()
            .Where(s => s.Id == p.SavedSearchId)
            .Select(s => new { s.UserId, s.Name, s.NotifyChannel })
            .FirstOrDefaultAsync(ct)
            ?? throw new OutboxPermanentException("Сохранённый поиск не найден.");

        // Канал мог быть выключен после постановки сообщения в очередь — тогда молча закрываем.
        if (search.NotifyChannel == SavedSearchNotifyChannel.None)
            return;

        // Тянем объявления по id, сохраняя порядок payload; исчезнувшие/удалённые просто пропускаем.
        var listings = await db.Listings.AsNoTracking()
            .Where(l => p.ListingIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Title, l.Price, l.PriceType, l.City, l.Slug })
            .ToListAsync(ct);

        if (listings.Count == 0)
            return; // все совпадения уже неактуальны — слать нечего

        var byId = listings.ToDictionary(l => l.Id);
        var lines = p.ListingIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .Select(l => $"• {l.Title} — {FormatPrice(l.PriceType, l.Price)} ({l.City})\n  /listing/{l.Slug}");

        var body = $"По вашему поиску «{search.Name}» появились новые объявления:\n\n"
                   + string.Join("\n", lines);

        var channel = search.NotifyChannel == SavedSearchNotifyChannel.Telegram
            ? NotificationChannel.Telegram
            : NotificationChannel.Email;

        await notifier.NotifyViaAsync(search.UserId, channel,
            $"Новые объявления по поиску «{search.Name}»", body, ct);
    }

    private static string FormatPrice(PriceType type, decimal? price) => type switch
    {
        PriceType.Free => "Бесплатно",
        PriceType.Negotiable => "Цена договорная",
        _ => $"{price:N0} руб."
    };
}

/// <summary>Удаление объектов из хранилища (MinIO). Payload — JSON-массив ключей.</summary>
public sealed class DeleteImagesHandler(IObjectStorage storage) : IOutboxHandler
{
    public string Type => OutboxMessage.DeleteImages;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var keys = OutboxPayload.Parse<string[]>(message.Payload);
        // RemoveObject у MinIO идемпотентен: отсутствующий объект не ошибка.
        foreach (var key in keys)
            await storage.RemoveAsync(key, ct);
    }
}

/// <summary>Legacy-удаление одного объекта: payload — ключ строкой (сообщения до объединения).</summary>
public sealed class DeleteObjectHandler(IObjectStorage storage) : IOutboxHandler
{
    public string Type => OutboxMessage.DeleteObject;

    public Task HandleAsync(OutboxMessage message, CancellationToken ct) =>
        storage.RemoveAsync(message.Payload, ct);
}
