using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
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

    /// <summary>Адрес отправителя. При отправке через Gmail Gmail принудительно
    /// подставит адрес аутентифицированного аккаунта.</summary>
    public string From { get; set; } = "no-reply@genesis-hq.com";

    /// <summary>Отображаемое имя отправителя (получатель видит его вместо адреса).</summary>
    public string FromName { get; set; } = "Genesis Market";

    public bool UseSsl { get; set; } = true;
}

/// <summary>Встроенное (inline, cid:) изображение письма.</summary>
public sealed record InlineImage(string ContentId, byte[] Content, string MediaType);

public interface IEmailSender
{
    Task SendAsync(
        string toEmail, string subject, string htmlBody, string textBody,
        InlineImage? inlineImage, CancellationToken ct);
}

/// <summary>
/// Отправка письма через SMTP (System.Net.Mail). multipart/alternative:
/// text + html; при наличии — inline-логотип как LinkedResource (cid).
/// Хост/порт/отправитель из конфигурации, логин/пароль — из env.
/// </summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    private readonly SmtpOptions _o = options.Value;

    public async Task SendAsync(
        string toEmail, string subject, string htmlBody, string textBody,
        InlineImage? inlineImage, CancellationToken ct)
    {
        using var client = new SmtpClient(_o.Host, _o.Port) { EnableSsl = _o.UseSsl };
        if (!string.IsNullOrEmpty(_o.User))
            client.Credentials = new NetworkCredential(_o.User, _o.Password);

        using var message = new MailMessage
        {
            From = new MailAddress(_o.From, _o.FromName, Encoding.UTF8),
            Subject = subject,
            SubjectEncoding = Encoding.UTF8
        };
        message.To.Add(toEmail);

        var textView = AlternateView.CreateAlternateViewFromString(
            textBody, Encoding.UTF8, MediaTypeNames.Text.Plain);
        var htmlView = AlternateView.CreateAlternateViewFromString(
            htmlBody, Encoding.UTF8, MediaTypeNames.Text.Html);

        if (inlineImage is not null)
        {
            var logo = new LinkedResource(new MemoryStream(inlineImage.Content), inlineImage.MediaType)
            {
                ContentId = inlineImage.ContentId,
                TransferEncoding = TransferEncoding.Base64
            };
            htmlView.LinkedResources.Add(logo);
        }

        // Порядок важен: text первым, html последним (клиент выбирает лучший).
        message.AlternateViews.Add(textView);
        message.AlternateViews.Add(htmlView);

        await client.SendMailAsync(message, ct);
    }
}

/// <summary>
/// Dev-реализация: не отправляет письмо, а пишет текстовую версию в лог,
/// чтобы код был виден локально. Используется, когда SMTP не сконфигурирован.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(
        string toEmail, string subject, string htmlBody, string textBody,
        InlineImage? inlineImage, CancellationToken ct)
    {
        logger.LogWarning("[DEV EMAIL] Кому: {Email} | Тема: {Subject} | Текст: {Body}",
            toEmail, subject, textBody);
        return Task.CompletedTask;
    }
}
