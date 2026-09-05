using System.Collections.Concurrent;
using GenesisMarket.Api.Auth;

namespace GenesisMarket.Tests;

/// <summary>Письмо, «отправленное» в тесте.</summary>
public sealed record CapturedEmail(string To, string Subject, string Html, string Text);

/// <summary>
/// Тестовый двойник SMTP: ничего не шлёт, запоминает последнее письмо по адресу,
/// чтобы тест мог достать из него ссылку восстановления.
/// </summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentDictionary<string, CapturedEmail> _sent = new();

    public Task SendAsync(
        string toEmail, string subject, string htmlBody, string textBody,
        InlineImage? inlineImage, CancellationToken ct)
    {
        _sent[toEmail] = new CapturedEmail(toEmail, subject, htmlBody, textBody);
        return Task.CompletedTask;
    }

    public CapturedEmail? Last(string toEmail) =>
        _sent.TryGetValue(toEmail, out var mail) ? mail : null;
}
