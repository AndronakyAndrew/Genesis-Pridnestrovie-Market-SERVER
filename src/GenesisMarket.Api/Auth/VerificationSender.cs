using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Auth;

/// <summary>
/// Доставка кода подтверждения нужным каналом. Скрывает выбор транспорта
/// (SMS/e-mail) от логики подтверждения.
/// </summary>
public interface IVerificationSender
{
    Task SendCodeAsync(VerificationChannel channel, string target, string code, int ttlMinutes, CancellationToken ct);
}

public sealed class VerificationSender(ISmsSender sms, IEmailSender email) : IVerificationSender
{
    public Task SendCodeAsync(
        VerificationChannel channel, string target, string code, int ttlMinutes, CancellationToken ct) =>
        channel switch
        {
            VerificationChannel.Phone => sms.SendAsync(
                target,
                $"Genesis Market: код подтверждения {code}. Действует {ttlMinutes} мин.",
                ct),
            VerificationChannel.Email => email.SendAsync(
                target,
                "Genesis Market — подтверждение почты",
                $"Ваш код подтверждения: {code}. Действует {ttlMinutes} мин.",
                ct),
            _ => Task.CompletedTask
        };
}
