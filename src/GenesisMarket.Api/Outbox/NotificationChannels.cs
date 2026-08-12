using System.Net.Http.Json;
using System.Text.Encodings.Web;
using GenesisMarket.Api.Auth;
using GenesisMarket.Domain.Enums;
using Microsoft.Extensions.Options;

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

/// <summary>Настройки Telegram-бота. Секция <c>Telegram</c>. Токен — только из env.</summary>
public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    /// <summary>Токен бота (<c>Telegram__BotToken</c>). Пусто ⇒ используется dev-лог вместо отправки.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>
    /// Идентификатор публичного канала/чата для постов об опубликованных объявлениях (шаг 16).
    /// Пусто ⇒ доставка <c>ListingPublished</c> невозможна.
    /// </summary>
    public string BroadcastChatId { get; set; } = "";
}

/// <summary>Минимальный клиент Telegram Bot API (sendMessage).</summary>
public interface ITelegramClient
{
    Task SendMessageAsync(string chatId, string text, CancellationToken ct);

    /// <summary>Есть ли публичный канал для постов (для обработчика <c>ListingPublished</c>).</summary>
    string? BroadcastChatId { get; }
}

/// <summary>Боевой клиент: шлёт sendMessage через HTTP. Ошибку транслирует исключением (ретрай).</summary>
public sealed class HttpTelegramClient(HttpClient http, IOptions<TelegramOptions> options) : ITelegramClient
{
    private readonly TelegramOptions _o = options.Value;

    public string? BroadcastChatId =>
        string.IsNullOrWhiteSpace(_o.BroadcastChatId) ? null : _o.BroadcastChatId;

    public async Task SendMessageAsync(string chatId, string text, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{_o.BotToken}/sendMessage";
        using var response = await http.PostAsJsonAsync(
            url, new { chat_id = chatId, text, disable_web_page_preview = true }, ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Telegram sendMessage вернул {(int)response.StatusCode}.");
    }
}

/// <summary>Dev-клиент: не ходит в сеть, пишет в лог (когда токен не задан).</summary>
public sealed class LogTelegramClient(ILogger<LogTelegramClient> logger) : ITelegramClient
{
    public string? BroadcastChatId => "dev-broadcast";

    public Task SendMessageAsync(string chatId, string text, CancellationToken ct)
    {
        logger.LogWarning("[DEV TELEGRAM] chatId={ChatId} | {Text}", chatId, text);
        return Task.CompletedTask;
    }
}

/// <summary>Telegram-канал уведомлений: адрес — chatId пользователя.</summary>
public sealed class TelegramNotificationChannel(ITelegramClient client) : INotificationChannel
{
    public NotificationChannel Kind => NotificationChannel.Telegram;

    public Task SendAsync(string address, string subject, string body, CancellationToken ct) =>
        client.SendMessageAsync(address, $"{subject}\n\n{body}", ct);
}
