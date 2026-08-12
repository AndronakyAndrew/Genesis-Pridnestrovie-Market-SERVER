using System.Text.Json;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

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

/// <summary>Объявление опубликовано → пост в публичный Telegram-канал (шаг 16).</summary>
public sealed class ListingPublishedHandler(AppDbContext db, ITelegramClient telegram) : IOutboxHandler
{
    public string Type => OutboxMessage.ListingPublished;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var chat = telegram.BroadcastChatId
            ?? throw new OutboxPermanentException("Публичный Telegram-канал не сконфигурирован (Telegram:BroadcastChatId).");

        var p = OutboxPayload.Parse<ListingPublishedPayload>(message.Payload);
        var listing = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == p.ListingId)
            .Select(l => new { l.Title, l.Price, l.PriceType, l.City, l.Slug })
            .FirstOrDefaultAsync(ct)
            ?? throw new OutboxPermanentException("Объявление не найдено.");

        var price = listing.PriceType switch
        {
            PriceType.Free => "Бесплатно",
            PriceType.Negotiable => "Цена договорная",
            _ => $"{listing.Price:N0} руб."
        };

        var text = $"🆕 {listing.Title}\n{price}\nГород: {listing.City}\n/listing/{listing.Slug}";
        await telegram.SendMessageAsync(chat, text, ct);
    }
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
