using System.Net;
using System.Text.Json;
using GenesisMarket.Api.Feedback;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Юнит-тесты <see cref="ResendEmailService"/> поверх фейкового <see cref="HttpMessageHandler"/>
/// (без реальной сети к Resend) — проверяют экранирование содержимого письма (защита от
/// HTML-инъекции в письмо сотруднику поддержки) и состав запроса.
/// </summary>
public class ResendEmailServiceTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"test-message-id\"}")
            };
        }
    }

    private static (ResendEmailService Service, CapturingHandler Handler) CreateService()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com/") };
        var options = Options.Create(new ResendOptions
        {
            ApiKey = "test-key",
            FromEmail = "Genesis Market <noreply@genesis-market.test>",
            NotificationEmail = "feedback@genesis-market.test"
        });
        var service = new ResendEmailService(http, options, NullLogger<ResendEmailService>.Instance);
        return (service, handler);
    }

    [Fact]
    public async Task Html_tags_in_message_are_escaped_not_injected()
    {
        var (service, handler) = CreateService();
        var feedback = new FeedbackMessage
        {
            Type = FeedbackType.Bug,
            Name = "<b>Имя</b>",
            Contact = "user@example.com",
            Message = "<script>alert(1)</script>\nВторая строка"
        };

        await service.SendFeedbackNotificationAsync(feedback, CancellationToken.None);

        Assert.NotNull(handler.LastBody);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        var html = doc.RootElement.GetProperty("html").GetString()!;

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<b>Имя</b>", html); // имя тоже экранировано, не только сообщение
        Assert.Contains("&lt;b&gt;", html);
        Assert.Contains("<br>", html); // перенос строки — наш безопасный литеральный тег, не из ввода
    }

    [Fact]
    public async Task Sends_bearer_token_and_configured_sender_and_recipient()
    {
        var (service, handler) = CreateService();
        var feedback = new FeedbackMessage
        {
            Type = FeedbackType.General,
            Contact = "user@example.com",
            Message = "Привет"
        };

        await service.SendFeedbackNotificationAsync(feedback, CancellationToken.None);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization.Parameter);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("Genesis Market <noreply@genesis-market.test>", doc.RootElement.GetProperty("from").GetString());
        Assert.Equal("feedback@genesis-market.test", doc.RootElement.GetProperty("to")[0].GetString());
        Assert.Contains("Новое обращение", doc.RootElement.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Confirmation_is_sent_to_the_submitter_contact()
    {
        var (service, handler) = CreateService();
        var feedback = new FeedbackMessage
        {
            Type = FeedbackType.General,
            Contact = "submitter@example.com",
            Message = "Привет"
        };

        await service.SendFeedbackConfirmationAsync(feedback, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("submitter@example.com", doc.RootElement.GetProperty("to")[0].GetString());
    }
}
