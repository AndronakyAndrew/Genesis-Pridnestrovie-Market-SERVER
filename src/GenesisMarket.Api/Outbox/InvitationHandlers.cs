using GenesisMarket.Api.Seo;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Outbox;

public sealed record MoveImagePair(string From, string To);

/// <summary>
/// Перенос объектов хранилища: копия под новый ключ, затем удаление исходного.
/// Идемпотентен: при повторе после частичного успеха исходного объекта уже может не быть —
/// тогда проверяем, что цель на месте, и идём дальше. Нет ни того, ни другого — ошибка
/// без ретраев: фото потеряно, и повтор его не вернёт.
/// </summary>
public sealed class MoveImagesHandler(IObjectStorage storage, ILogger<MoveImagesHandler> logger) : IOutboxHandler
{
    public string Type => OutboxMessage.MoveImages;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var pairs = OutboxPayload.Parse<MoveImagePair[]>(message.Payload);

        // Сначала все копии, потом все удаления: сбой посередине не оставит объявление
        // с частью фото, которых уже нет ни в старом, ни в новом месте.
        foreach (var pair in pairs)
        {
            try
            {
                await storage.CopyAsync(pair.From, pair.To, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Прошлый прогон уже скопировал и удалил исходный объект — цель на месте.
                if (await ExistsAsync(pair.To, ct))
                    continue;
                if (ex is FileNotFoundException || !await ExistsAsync(pair.From, ct))
                {
                    logger.LogError("Перенос фото: нет ни исходного {From}, ни целевого {To} объекта.", pair.From, pair.To);
                    throw new OutboxPermanentException($"Объект {pair.From} не найден.");
                }
                throw; // временный сбой хранилища — ретрай по общему расписанию
            }
        }

        foreach (var pair in pairs)
            await storage.RemoveAsync(pair.From, ct);
    }

    private async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await using var _ = await storage.GetAsync(key, ct);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>
/// Приглашение принято → письмо владельцу: черновики в кабинете, без его подтверждения
/// ничего не публикуется. Письмо — всегда почтой (адрес подтверждён кодом при входе),
/// не каналом профиля: профиль только что создан и выбора там ещё не было.
/// </summary>
public sealed class InvitationAcceptedHandler(
    AppDbContext db,
    IUserNotifier notifier,
    IOptions<SeoOptions> seo) : IOutboxHandler
{
    public string Type => OutboxMessage.InvitationAccepted;

    private sealed record Payload(Guid InvitationId);

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var p = OutboxPayload.Parse<Payload>(message.Payload);
        var invitation = await db.SellerInvitations.AsNoTracking()
            .Where(i => i.Id == p.InvitationId && i.Status == InvitationStatus.Accepted)
            .Select(i => new { i.ShopName, i.AcceptedByUserId, Drafts = i.Drafts.Count })
            .FirstOrDefaultAsync(ct)
            ?? throw new OutboxPermanentException("Принятое приглашение не найдено.");

        var link = seo.Value.NormalizedBaseUrl is { } site ? $"\n\nОткрыть кабинет: {site}/profile/invited" : "";
        await notifier.NotifyViaAsync(invitation.AcceptedByUserId!.Value, NotificationChannel.Email,
            $"Магазин «{invitation.ShopName}» подключён к Местной площадке Genesis",
            $"Вы приняли приглашение для магазина «{invitation.ShopName}». В вашем кабинете " +
            $"{invitation.Drafts} черновиков товаров.\n\n" +
            "Без вашего подтверждения ничего не публикуется: проверьте товары, укажите свои контакты " +
            "и отправьте выбранные объявления на публикацию." + link, ct);
    }
}
