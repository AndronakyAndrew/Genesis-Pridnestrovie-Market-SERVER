using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Auth;

/// <summary>Настройки SMTP. Секция <c>Smtp</c>. User/Password — только из env.</summary>
public sealed class SmtpOptions
{
    public const string Section = "Smtp";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "no-reply@genesis-market.md";
    public bool UseSsl { get; set; } = true;
}

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string body, CancellationToken ct);
}

/// <summary>
/// Отправка письма через SMTP (System.Net.Mail.SmtpClient) — простой вариант
/// на старте. Хост/порт/отправитель из конфигурации, логин/пароль — из env.
/// Позже можно заменить на MailKit/провайдер, не трогая вызывающий код.
/// </summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    private readonly SmtpOptions _o = options.Value;

    public async Task SendAsync(string toEmail, string subject, string body, CancellationToken ct)
    {
        using var client = new SmtpClient(_o.Host, _o.Port) { EnableSsl = _o.UseSsl };
        if (!string.IsNullOrEmpty(_o.User))
            client.Credentials = new NetworkCredential(_o.User, _o.Password);

        using var message = new MailMessage(_o.From, toEmail, subject, body);
        await client.SendMailAsync(message, ct);
    }
}

/// <summary>
/// Dev-реализация: не отправляет письмо, а пишет его в лог (Warning),
/// чтобы код был виден локально. Используется, когда SMTP не сконфигурирован.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string body, CancellationToken ct)
    {
        logger.LogWarning("[DEV EMAIL] Кому: {Email} | Тема: {Subject} | Текст: {Body}",
            toEmail, subject, body);
        return Task.CompletedTask;
    }
}
