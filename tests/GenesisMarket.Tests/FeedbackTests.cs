using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GenesisMarket.Tests;

/// <summary>
/// Форма обратной связи: приём анонимных обращений, доставка письма-уведомления и
/// письма-подтверждения через outbox/Resend, устойчивость приёма к сбою Resend.
/// </summary>
public class FeedbackTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Feedback_with_email_contact_gets_notification_and_confirmation()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/feedback", new
        {
            type = "Bug",
            name = "Иван",
            contact = "ivan@example.com",
            message = "Не открывается карточка объявления"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var feedbackId = body.GetProperty("id").GetGuid();

        var result = await factory.RunOutboxAsync();
        Assert.True(result.Delivered >= 1);

        Assert.Contains(factory.Resend.Notifications, n => n.FeedbackId == feedbackId && n.Message.Contains("карточка"));
        Assert.Contains(factory.Resend.Confirmations, c => c.FeedbackId == feedbackId);
    }

    [Fact]
    public async Task Feedback_with_phone_contact_does_not_get_confirmation()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/feedback", new
        {
            type = "General",
            contact = "+37377012345",
            message = "Вопрос по объявлению"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var feedbackId = body.GetProperty("id").GetGuid();

        await factory.RunOutboxAsync();

        Assert.Contains(factory.Resend.Notifications, n => n.FeedbackId == feedbackId);
        Assert.DoesNotContain(factory.Resend.Confirmations, c => c.FeedbackId == feedbackId);
    }

    [Fact]
    public async Task Resend_failure_does_not_fail_the_request_and_row_is_kept()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/feedback", new
        {
            type = "Complaint",
            contact = "someone@example.com",
            message = "Обращение, для которого Resend временно недоступен"
        });
        // Приём обращения не зависит от Resend вообще: письмо шлётся фоном через outbox.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var feedbackId = body.GetProperty("id").GetGuid();
        Assert.True(await factory.FeedbackExistsAsync(feedbackId));

        factory.Resend.FailNotificationWith = new InvalidOperationException("Resend недоступен (симуляция)");
        try
        {
            var result = await factory.RunOutboxAsync();
            // Сбой доставки не бросает исключение из диспетчера — сообщение уходит на ретрай.
            Assert.True(result.Retrying >= 1 || result.Failed >= 1);
        }
        finally
        {
            factory.Resend.FailNotificationWith = null;
        }

        // Запись обращения в БД не пострадала от сбоя отправки письма.
        Assert.True(await factory.FeedbackExistsAsync(feedbackId));
    }

    [Fact]
    public async Task Invalid_request_without_contact_returns_400()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/feedback", new
        {
            type = "General",
            message = "Без контакта"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
