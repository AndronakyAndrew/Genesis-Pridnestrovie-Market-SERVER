using GenesisMarket.Domain.Entities;

namespace GenesisMarket.Api.Feedback;

/// <summary>
/// Dev-реализация: не отправляет письмо, а пишет его в лог, чтобы обращение было видно
/// локально без настроенного Resend:ApiKey. Используется, когда ключ не сконфигурирован.
/// </summary>
public sealed class LogResendEmailService(ILogger<LogResendEmailService> logger) : IResendEmailService
{
    public Task SendFeedbackNotificationAsync(FeedbackMessage feedback, CancellationToken ct)
    {
        logger.LogWarning(
            "[DEV RESEND] Уведомление по обращению {FeedbackId} ({Type}) от {Name} <{Contact}>: {Message}",
            feedback.Id, feedback.Type, feedback.Name ?? "—", feedback.Contact, feedback.Message);
        return Task.CompletedTask;
    }

    public Task SendFeedbackConfirmationAsync(FeedbackMessage feedback, CancellationToken ct)
    {
        logger.LogWarning(
            "[DEV RESEND] Подтверждение по обращению {FeedbackId} отправителю {Contact}",
            feedback.Id, feedback.Contact);
        return Task.CompletedTask;
    }
}
