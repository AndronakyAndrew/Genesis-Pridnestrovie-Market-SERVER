using System.Text.Json;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Security;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Приём обращений из формы обратной связи. Открыт анонимам. После сохранения ставит
/// в outbox сообщение-уведомление на служебный адрес — доставка письма (Resend) идёт
/// фоном, отдельно от HTTP-ответа, и не может завалить этот запрос.
/// </summary>
[Route("api/feedback")]
public class FeedbackController(AppDbContext db) : ApiControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Feedback)]
    [HttpPost]
    public async Task<ActionResult<FeedbackResponse>> Create(CreateFeedbackRequest request, CancellationToken ct)
    {
        var feedback = new FeedbackMessage
        {
            Type = request.Type,
            Name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
            Contact = request.Contact.Trim(),
            Message = request.Message.Trim()
        };

        db.FeedbackMessages.Add(feedback);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.FeedbackReceived,
            Payload = JsonSerializer.Serialize(new { feedbackId = feedback.Id })
        });

        // Одна транзакция на обе записи: без принятого обращения уведомления не будет,
        // и наоборот — без успешного commit'а не останется "хвоста" в outbox.
        await db.SaveChangesAsync(ct);

        return Created((string?)null, Map(feedback));
    }

    private static FeedbackResponse Map(FeedbackMessage f) => new(f.Id, f.Type, f.CreatedAt);
}
