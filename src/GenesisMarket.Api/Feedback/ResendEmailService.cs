using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace GenesisMarket.Api.Feedback;

/// <summary>
/// Боевой клиент Resend (https://resend.com/docs/api-reference/emails/send-email) поверх
/// <see cref="HttpClient"/>. Содержимое обращения (Name/Contact/Message) экранируется перед
/// вставкой в HTML — иначе отправитель мог бы внедрить произвольную разметку в письмо
/// сотруднику поддержки. Один быстрый ретрай на сетевую ошибку/таймаут (не более 5с суммарно
/// на попытку+ретрай) — поверх этого доставку подстраховывает транзакционный outbox
/// (диспетчер сам повторит сообщение по своему расписанию, если и ретрай не помог).
/// </summary>
public sealed class ResendEmailService : IResendEmailService
{
    // Бюджет по конкретно этому HTTP-вызову: 2с попытка + 1с пауза + 2с ретрай ≈ 5с максимум.
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly ResendOptions _o;
    private readonly ILogger<ResendEmailService> _logger;
    private readonly AsyncRetryPolicy _retry;

    public ResendEmailService(HttpClient http, IOptions<ResendOptions> options, ILogger<ResendEmailService> logger)
    {
        _http = http;
        _o = options.Value;
        _logger = logger;

        _retry = Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(retryCount: 1, _ => RetryDelay, onRetryAsync: (ex, delay, attempt, _) =>
            {
                _logger.LogWarning("Повтор запроса к Resend (попытка {Attempt}) через {Delay}: {Reason}",
                    attempt, delay, ex.Message);
                return Task.CompletedTask;
            });
    }

    public Task SendFeedbackNotificationAsync(FeedbackMessage feedback, CancellationToken ct)
    {
        var subject = $"Новое обращение: {TypeLabel(feedback.Type)}";
        var html = BuildNotificationHtml(feedback);
        var text = BuildNotificationText(feedback);
        return SendAsync(_o.NotificationEmail, subject, html, text, feedback, ct);
    }

    public Task SendFeedbackConfirmationAsync(FeedbackMessage feedback, CancellationToken ct)
    {
        const string subject = "Мы получили ваше сообщение";
        var html = "<div style=\"font-family:Arial,sans-serif;font-size:15px;color:#0F1117\">"
                   + "<p>Здравствуйте!</p>"
                   + "<p>Мы получили ваше обращение в Genesis Market и ответим на указанный контакт, как только сможем.</p>"
                   + "</div>";
        const string text = "Здравствуйте!\n\nМы получили ваше обращение в Genesis Market и ответим на указанный контакт, как только сможем.";
        return SendAsync(feedback.Contact, subject, html, text, feedback, ct);
    }

    private async Task SendAsync(
        string to, string subject, string html, string text, FeedbackMessage feedback, CancellationToken ct)
    {
        try
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(AttemptTimeout + AttemptTimeout + RetryDelay);
            await _retry.ExecuteAsync(token => SendOnceAsync(to, subject, html, text, token), attemptCts.Token);

            _logger.LogInformation(
                "Письмо Resend по обращению {FeedbackId} ({Type}) отправлено", feedback.Id, feedback.Type);
            _logger.LogDebug("Resend: to={To} subject={Subject}", to, subject);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Не удалось отправить письмо Resend по обращению {FeedbackId} ({Type})", feedback.Id, feedback.Type);
            throw;
        }
    }

    private async Task SendOnceAsync(string to, string subject, string html, string text, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new
            {
                from = _o.FromEmail,
                to = new[] { to },
                subject,
                html,
                text
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(AttemptTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cts.Token);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TaskCanceledException("Таймаут запроса к Resend.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Resend вернул {(int)response.StatusCode}: {body}");
            }
        }
    }

    private static string BuildNotificationHtml(FeedbackMessage f)
    {
        // Экранируем перед вставкой в HTML — иначе теги/атрибуты в сообщении отправителя
        // могли бы сломать вёрстку письма или подменить ссылки (HTML-инъекция).
        var name = HtmlEncoder.Default.Encode(f.Name ?? "—");
        var contact = HtmlEncoder.Default.Encode(f.Contact);
        var message = string.Join("<br>", f.Message.Split('\n').Select(HtmlEncoder.Default.Encode));

        return "<div style=\"font-family:Arial,sans-serif;font-size:15px;color:#0F1117\">"
               + $"<p><b>Тип:</b> {HtmlEncoder.Default.Encode(TypeLabel(f.Type))}</p>"
               + $"<p><b>Имя:</b> {name}</p>"
               + $"<p><b>Контакт:</b> {contact}</p>"
               + $"<p><b>Сообщение:</b><br>{message}</p>"
               + "</div>";
    }

    private static string BuildNotificationText(FeedbackMessage f)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Тип: {TypeLabel(f.Type)}");
        sb.AppendLine($"Имя: {f.Name ?? "—"}");
        sb.AppendLine($"Контакт: {f.Contact}");
        sb.AppendLine("Сообщение:");
        sb.Append(f.Message);
        return sb.ToString();
    }

    private static string TypeLabel(FeedbackType type) => type switch
    {
        FeedbackType.Bug => "ошибка",
        FeedbackType.Complaint => "жалоба",
        FeedbackType.Partnership => "сотрудничество",
        _ => "общее"
    };
}
