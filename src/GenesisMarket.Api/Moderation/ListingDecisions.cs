using System.Text.Json;
using GenesisMarket.Api.Outbox.Telegram;
using GenesisMarket.Api.Telegram.Channel;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;

namespace GenesisMarket.Api.Moderation;

/// <summary>Решение модератора по объявлению в очереди.</summary>
public enum ListingDecision
{
    Approve,
    Reject,
    /// <summary>Вернуть автору на доработку (черновик с причиной, без штрафа доверия).</summary>
    Revise
}

/// <summary>
/// Единственное место, где решение по объявлению превращается в изменения: доменный
/// переход, уведомление автору (outbox), пост в Telegram-канале и запись в журнал.
/// Одиночные и пакетные ручки зовут одно и то же — пакет не может разойтись
/// с одиночным решением. Сохранение и транзакция — за вызывающим: так пакет
/// коммитится одной транзакцией вместе с журналом.
/// </summary>
public interface IListingDecisions
{
    /// <summary>
    /// Применить решение к отслеживаемому (tracked) объявлению, стоящему в очереди.
    /// Возвращает текст результата для модератора. Причина обязательна для Reject/Revise —
    /// проверку делает <see cref="ValidateReason"/>.
    /// </summary>
    Task<string> ApplyAsync(
        Listing listing, ListingDecision decision, RejectionReasonCode? reason, string? comment,
        CancellationToken ct);

    /// <summary>
    /// Ошибка валидации причины или null. Код Other без комментария ничего не объясняет:
    /// автор останется ровно в том же положении, что и с общим «отклонено».
    /// </summary>
    static string? ValidateReason(ListingDecision decision, RejectionReasonCode? reason, string? comment)
    {
        if (decision == ListingDecision.Approve)
            return null;
        if (reason is null)
            return "Укажите причину";
        if (reason == RejectionReasonCode.Other && string.IsNullOrWhiteSpace(comment))
            return "При причине «Другое» комментарий обязателен";
        return null;
    }
}

public sealed class ListingDecisions(
    AppDbContext db,
    IModerationAudit audit,
    IChannelPublisher channelPublisher) : IListingDecisions
{
    public async Task<string> ApplyAsync(
        Listing listing, ListingDecision decision, RejectionReasonCode? reason, string? comment,
        CancellationToken ct)
    {
        if (listing.ReviewQueuedAt is not { } queuedAt)
            throw new InvalidOperationException("Объявление не находится в очереди модерации.");

        // Постмодерация: объявление уже на витрине (и, возможно, в канале).
        var wasInCatalog = listing.Status == ListingStatus.Active;
        var note = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        var now = DateTimeOffset.UtcNow;
        var id = listing.Id;

        switch (decision)
        {
            case ListingDecision.Approve:
                listing.Approve(now);

                db.OutboxMessages.Add(new OutboxMessage
                {
                    Type = OutboxMessage.ListingApproved,
                    Payload = JsonSerializer.Serialize(new { listingId = id })
                });

                // Одобрение — момент анонса в канал в обоих режимах: при постмодерации
                // объявление до проверки в канал не уходит. Здесь только строка очереди
                // в этой же транзакции; пост отправит ChannelPublisherWorker.
                await channelPublisher.EnqueueAsync(id, ct);

                audit.Record(ModerationLog.ActionApproveListing, ModerationLog.TargetListing, id,
                    payload: new { postModeration = wasInCatalog }, waitSince: queuedAt);

                return wasInCatalog
                    ? "Объявление проверено и оставлено в каталоге."
                    : "Объявление одобрено и опубликовано.";

            case ListingDecision.Reject:
            case ListingDecision.Revise:
                var code = reason ?? throw new ArgumentException("Причина обязательна.", nameof(reason));
                var revise = decision == ListingDecision.Revise;

                if (revise)
                    listing.RequestRevision(code, note, now);
                else
                    listing.Reject(code, note, now);

                // Уведомление автору: только идентификаторы и причина, текст соберёт обработчик.
                db.OutboxMessages.Add(new OutboxMessage
                {
                    Type = revise ? OutboxMessage.ListingRevisionRequested : OutboxMessage.ListingRejected,
                    Payload = JsonSerializer.Serialize(new { listingId = id, reason = code.ToString(), comment = note })
                });

                // Постмодерация: пост в канале уже висит — помечаем снятым.
                if (wasInCatalog)
                    db.OutboxMessages.Add(new OutboxMessage
                    {
                        Type = OutboxMessage.ListingChannelUpdate,
                        Payload = JsonSerializer.Serialize(new { listingId = id, mark = ChannelMark.Archived })
                    });

                audit.Record(
                    revise ? ModerationLog.ActionReviseListing : ModerationLog.ActionRejectListing,
                    ModerationLog.TargetListing, id,
                    reason: note,
                    payload: new { reason = code.ToString(), comment = note, postModeration = wasInCatalog },
                    waitSince: queuedAt);

                return (revise, wasInCatalog) switch
                {
                    (true, true) => "Объявление снято с публикации и возвращено автору на доработку.",
                    (true, false) => "Объявление возвращено автору на доработку.",
                    (false, true) => "Объявление отклонено и снято с публикации, автор уведомлён.",
                    _ => "Объявление отклонено, автор уведомлён."
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(decision), decision, null);
        }
    }
}
