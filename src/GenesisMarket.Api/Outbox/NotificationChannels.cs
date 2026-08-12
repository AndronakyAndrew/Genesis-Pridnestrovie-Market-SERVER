using System.Text.Encodings.Web;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Outbox.Telegram;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Outbox;

/// <summary>
/// Канал доставки уведомления. Конкретный канал (почта/Telegram) выбирается диспетчером
/// уведомлений по настройке пользователя (<see cref="Domain.Entities.Profile.NotifyVia"/>).
/// </summary>
public interface INotificationChannel
{
    NotificationChannel Kind { get; }

    /// <summary>Отправить сообщение на адрес канала (email или Telegram chatId).</summary>
    Task SendAsync(string address, string subject, string body, CancellationToken ct);
}

/// <summary>Почтовый канал: оборачивает существующий <see cref="IEmailSender"/> (SMTP или dev-лог).</summary>
public sealed class EmailNotificationChannel(IEmailSender email) : INotificationChannel
{
    public NotificationChannel Kind => NotificationChannel.Email;

    public Task SendAsync(string address, string subject, string body, CancellationToken ct)
    {
        // Простое письмо: текстовая версия — как есть, html — экранированный текст в абзацах.
        var html = "<div style=\"font-family:Inter,Arial,sans-serif;font-size:15px;color:#0F1117\">"
                   + string.Concat(body.Split('\n').Select(line =>
                       $"<p>{HtmlEncoder.Default.Encode(line)}</p>"))
                   + "</div>";
        return email.SendAsync(address, subject, html, body, inlineImage: null, ct);
    }
}

/// <summary>
/// Telegram-канал уведомлений: адрес — личный chatId пользователя. Личные сообщения
/// шлём обычным текстом (без parse_mode): содержимое собирается сервером и не размечается.
/// </summary>
public sealed class TelegramNotificationChannel(ITelegramClient client) : INotificationChannel
{
    public NotificationChannel Kind => NotificationChannel.Telegram;

    public Task SendAsync(string address, string subject, string body, CancellationToken ct) =>
        client.SendMessageAsync(address, $"{subject}\n\n{body}", ct);
}
