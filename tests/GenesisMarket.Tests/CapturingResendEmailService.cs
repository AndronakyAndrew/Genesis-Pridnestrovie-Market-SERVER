using System.Collections.Concurrent;
using GenesisMarket.Api.Feedback;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Tests;

/// <summary>
/// Тестовый двойник Resend-клиента: не ходит в сеть, а записывает вызовы. Позволяет
/// эмулировать сбой Resend (<see cref="FailNotificationWith"/>) — для проверки, что outbox
/// это переживает и что POST /api/feedback никак от Resend не зависит.
/// </summary>
public sealed class CapturingResendEmailService : IResendEmailService
{
    public sealed record SentEmail(Guid FeedbackId, FeedbackType Type, string? Name, string Contact, string Message);

    private readonly ConcurrentQueue<SentEmail> _notifications = new();
    private readonly ConcurrentQueue<SentEmail> _confirmations = new();

    public IReadOnlyList<SentEmail> Notifications => _notifications.ToArray();
    public IReadOnlyList<SentEmail> Confirmations => _confirmations.ToArray();

    /// <summary>Если задано — очередной вызов SendFeedbackNotificationAsync бросает это исключение.</summary>
    public Exception? FailNotificationWith { get; set; }

    public Task SendFeedbackNotificationAsync(FeedbackMessage feedback, CancellationToken ct)
    {
        if (FailNotificationWith is { } ex)
            throw ex;

        _notifications.Enqueue(new SentEmail(feedback.Id, feedback.Type, feedback.Name, feedback.Contact, feedback.Message));
        return Task.CompletedTask;
    }

    public Task SendFeedbackConfirmationAsync(FeedbackMessage feedback, CancellationToken ct)
    {
        _confirmations.Enqueue(new SentEmail(feedback.Id, feedback.Type, feedback.Name, feedback.Contact, feedback.Message));
        return Task.CompletedTask;
    }
}
