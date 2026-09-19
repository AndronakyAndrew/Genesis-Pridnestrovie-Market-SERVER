using GenesisMarket.Api.Business;
using GenesisMarket.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Заявки на подтверждение бизнеса — часть рабочей поверхности модератора:
/// тот же префикс <c>api/moderation</c>, та же policy «Moderator» (роль из claim JWT),
/// тот же журнал <c>moderation_logs</c> и тот же ответ на действие
/// (<see cref="ModerationActionResult"/>), что у <see cref="ModerationController"/>.
/// Идентификатор заявки — Id пользователя: заявка у аккаунта одна.
/// </summary>
[Route("api/moderation/business")]
[Authorize(Policy = "Moderator")]
public class BusinessModerationController(IBusinessVerificationService verification) : ApiControllerBase
{
    /// <summary>
    /// Список заявок (по умолчанию Pending), FIFO по моменту подачи, курсорная пагинация.
    /// Реквизиты — целиком: по ним модератор сверяется с реестром.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<BusinessApplicationsPage>> List(
        [FromQuery] BusinessApplicationsQuery query, CancellationToken ct)
    {
        var (page, error) = await verification.ListAsync(query, ct);
        return error is not null ? this.ToProblem(error) : Ok(page);
    }

    /// <summary>Одобрить: Pending → Verified. В теле — <c>submittedAt</c> заявки из списка (версия).</summary>
    [HttpPost("{userId:guid}/approve")]
    public async Task<ActionResult<ModerationActionResult>> Approve(
        Guid userId, ApproveBusinessRequest request, CancellationToken ct)
    {
        var error = await verification.ApproveAsync(userId, request.SubmittedAt, ct);
        return error is not null
            ? this.ToProblem(error)
            : Ok(new ModerationActionResult("Бизнес подтверждён."));
    }

    /// <summary>Отклонить с причиной: Pending → Rejected. Причину увидит пользователь.</summary>
    [HttpPost("{userId:guid}/reject")]
    public async Task<ActionResult<ModerationActionResult>> Reject(
        Guid userId, RejectBusinessRequest request, CancellationToken ct)
    {
        var error = await verification.RejectAsync(userId, request.SubmittedAt, request.Reason, ct);
        return error is not null
            ? this.ToProblem(error)
            : Ok(new ModerationActionResult("Заявка отклонена."));
    }
}
