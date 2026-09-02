using GenesisMarket.Domain.Entities;

namespace GenesisMarket.Api.Feedback;

/// <summary>Отправка писем по обращениям формы обратной связи через Resend.</summary>
public interface IResendEmailService
{
    /// <summary>Уведомление на служебный адрес (<see cref="ResendOptions.NotificationEmail"/>).</summary>
    Task SendFeedbackNotificationAsync(FeedbackMessage feedback, CancellationToken ct);

    /// <summary>Короткое подтверждение отправителю — вызывать только если Contact похож на email.</summary>
    Task SendFeedbackConfirmationAsync(FeedbackMessage feedback, CancellationToken ct);
}
