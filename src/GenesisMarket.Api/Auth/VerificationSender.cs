using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Auth;

/// <summary>
/// Доставка кода подтверждения нужным каналом. Скрывает выбор транспорта
/// (SMS/e-mail) от логики подтверждения. Для e-mail рендерится HTML-шаблон
/// с встроенным логотипом; для телефона — простой текст.
/// </summary>
public interface IVerificationSender
{
    Task SendCodeAsync(VerificationChannel channel, string target, string code, int ttlMinutes, CancellationToken ct);
}

public sealed class VerificationSender(
    ISmsSender sms,
    IEmailSender email,
    VerificationEmailRenderer emailRenderer) : IVerificationSender
{
    public Task SendCodeAsync(
        VerificationChannel channel, string target, string code, int ttlMinutes, CancellationToken ct) =>
        channel switch
        {
            VerificationChannel.Phone => sms.SendAsync(
                target,
                $"Местная площадка Genesis: код подтверждения {code}. Действует {ttlMinutes} мин.",
                ct),
            VerificationChannel.Email => SendEmailAsync(target, emailRenderer.RenderCodeEmail(code), ct),
            VerificationChannel.LoginEmail => SendEmailAsync(target, emailRenderer.RenderLoginCodeEmail(code), ct),
            VerificationChannel.EmailChange =>
                SendEmailAsync(target, emailRenderer.RenderEmailChangeCodeEmail(code), ct),
            VerificationChannel.AccountDeletion =>
                SendEmailAsync(target, emailRenderer.RenderAccountDeletionCodeEmail(code), ct),
            _ => Task.CompletedTask
        };

    private Task SendEmailAsync(string target, RenderedEmail mail, CancellationToken ct)
    {
        return email.SendAsync(target, mail.Subject, mail.Html, mail.Text, mail.Logo, ct);
    }
}
