using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Outbox;

/// <summary>
/// Доставляет уведомление конкретному пользователю выбранным им каналом. Адресата и
/// его настройки читает из БД по id (в payload персональных данных нет). Канал
/// выбирается по <see cref="Domain.Entities.Profile.NotifyVia"/> с деградацией к почте,
/// если Telegram не привязан.
/// </summary>
public interface IUserNotifier
{
    Task NotifyAsync(Guid userId, string subject, string body, CancellationToken ct);
}

public sealed class UserNotifier(
    AppDbContext db,
    IEnumerable<INotificationChannel> channels,
    ILogger<UserNotifier> logger) : IUserNotifier
{
    public async Task NotifyAsync(Guid userId, string subject, string body, CancellationToken ct)
    {
        var recipient = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDeleted)
            .Select(u => new
            {
                u.Email,
                NotifyVia = u.Profile!.NotifyVia,
                u.Profile.TelegramChatId
            })
            .FirstOrDefaultAsync(ct);

        if (recipient is null)
            // Удалённый/несуществующий адресат — доставлять некому и незачем повторять.
            throw new OutboxPermanentException("Адресат уведомления не найден или удалён.");

        var (kind, address) =
            recipient.NotifyVia == NotificationChannel.Telegram && recipient.TelegramChatId is { } chatId
                ? (NotificationChannel.Telegram, chatId.ToString())
                : (NotificationChannel.Email, recipient.Email);

        if (string.IsNullOrWhiteSpace(address))
            throw new OutboxPermanentException("У адресата нет доступного канала уведомлений.");

        var channel = channels.FirstOrDefault(c => c.Kind == kind)
            ?? throw new OutboxPermanentException($"Канал {kind} не зарегистрирован.");

        await channel.SendAsync(address, subject, body, ct);

        // Лог без персональных данных: только id адресата и канал (без адреса/тела).
        logger.LogInformation("Уведомление доставлено: userId={UserId} канал={Channel}", userId, kind);
    }
}
