using System.Text.Json;
using System.Text.RegularExpressions;
using GenesisMarket.Api.Feedback;
using GenesisMarket.Api.Outbox.Telegram;
using GenesisMarket.Api.Telegram.Channel;
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

    /// <summary>
    /// Текст причины для письма. Разбирается как <see cref="RejectionReasonCode"/>,
    /// но старые значения <see cref="ReportReason"/> тоже понимаются: в очереди
    /// outbox могут лежать сообщения, записанные до смены набора кодов.
    /// </summary>
    internal static string ReasonText(string reason)
    {
        if (Enum.TryParse<RejectionReasonCode>(reason, out var code))
            return code switch
            {
                RejectionReasonCode.Duplicate => "дубликат объявления",
                RejectionReasonCode.ProhibitedItem => "запрещённый товар или услуга",
                RejectionReasonCode.WrongCategory => "неверная категория",
                RejectionReasonCode.BadPhotos => "фотографии не подходят",
                RejectionReasonCode.ContactsInText => "контакты в заголовке или описании",
                RejectionReasonCode.PriceViolation => "нарушение в цене",
                _ => "нарушение правил"
            };

        return Enum.TryParse<ReportReason>(reason, out var legacy) ? legacy switch
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
}

/// <summary>
/// Возвращено на доработку → автору: что исправить и что объявление ждёт его в черновиках.
/// Payload тот же, что у отказа; тексты причин — общие с <see cref="ListingRejectedHandler"/>.
/// </summary>
public sealed class ListingRevisionRequestedHandler(AppDbContext db, IUserNotifier notifier) : IOutboxHandler
{
    public string Type => OutboxMessage.ListingRevisionRequested;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<ListingRejectedPayload>(message.Payload);
        var listing = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Id == p.ListingId)
            .Select(l => new { l.OwnerId, l.Title })
            .FirstOrDefaultAsync(ct)
            ?? throw new OutboxPermanentException("Объявление не найдено.");

        var body = $"Модератор вернул объявление «{listing.Title}» на доработку. " +
                   $"Что исправить: {ListingRejectedHandler.ReasonText(p.Reason)}.";
        if (!string.IsNullOrWhiteSpace(p.Comment))
            body += $"\nКомментарий модератора: {p.Comment}";
        body += "\nОбъявление лежит в черновиках: исправьте его и опубликуйте снова.";

        await notifier.NotifyAsync(listing.OwnerId, "Объявление нужно доработать", body, ct);
    }
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
/// Объявление стало одобренно-активным → в очередь публикации в канал. Сам пост отправит
/// <c>ChannelPublisherWorker</c> с учётом интервала и рабочего окна. Строка очереди сохраняется
/// в транзакции диспетчера. Продюсеры теперь зовут <see cref="IChannelPublisher.EnqueueAsync"/>
/// напрямую; обработчик остаётся для сообщений, записанных до перехода на очередь.
/// </summary>
public sealed class ListingPublishedHandler(IChannelPublisher channel) : IOutboxHandler
{
    public string Type => OutboxMessage.ListingPublished;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<ListingPublishedPayload>(message.Payload);
        await channel.EnqueueAsync(p.ListingId, ct);
    }
}

/// <summary>
/// Правка ранее опубликованного поста: «Продано», «Снято с публикации» или возврат полной подписи.
/// Через outbox — чтобы HTTP-запрос не ходил в Telegram и правка не ушла по откатанной транзакции.
/// Если объявление в канал не постили или пост удалён вручную — завершается без ошибки.
/// </summary>
public sealed class ListingChannelUpdateHandler(IChannelPublisher channel) : IOutboxHandler
{
    public string Type => OutboxMessage.ListingChannelUpdate;

    public Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<ListingChannelUpdatePayload>(message.Payload);

        return p.Mark switch
        {
            ChannelMark.Sold => channel.MarkSoldAsync(p.ListingId, ct),
            ChannelMark.Archived => channel.MarkRemovedAsync(p.ListingId, ct),
            ChannelMark.Active => channel.RestoreAsync(p.ListingId, ct),
            _ => throw new OutboxPermanentException($"Неизвестная пометка поста '{p.Mark}'.")
        };
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

/// <summary>
/// Новое обращение из формы обратной связи → письмо-уведомление на служебный адрес
/// (<see cref="IResendEmailService.SendFeedbackNotificationAsync"/>) и, если контакт похож
/// на email, короткое письмо-подтверждение отправителю. Сбой отправки — временный (ретрай
/// диспетчера по общему расписанию); адресат/контент не зависят от состояния объявления,
/// поэтому это единственный обработчик, которому не нужен <see cref="IUserNotifier"/>.
/// </summary>
public sealed partial class FeedbackReceivedHandler(AppDbContext db, IResendEmailService email) : IOutboxHandler
{
    public string Type => OutboxMessage.FeedbackReceived;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<FeedbackReceivedPayload>(message.Payload);
        var feedback = await db.FeedbackMessages.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == p.FeedbackId, ct)
            ?? throw new OutboxPermanentException("Обращение не найдено.");

        await email.SendFeedbackNotificationAsync(feedback, ct);

        if (LooksLikeEmail(feedback.Contact))
            await email.SendFeedbackConfirmationAsync(feedback, ct);
    }

    // Простая проверка формата: телефоны (цифры, +, скобки, дефисы) её не проходят.
    private static bool LooksLikeEmail(string contact) => EmailLikeRegex().IsMatch(contact.Trim());

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailLikeRegex();
}
