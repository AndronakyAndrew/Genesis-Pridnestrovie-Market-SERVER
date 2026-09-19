using FluentValidation;
using GenesisMarket.Api.Business;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Бизнес-режим владельца. Отдельной регистрации для бизнеса нет — это режим
/// обычного аккаунта:
/// <list type="bullet">
/// <item><c>PUT</c> — сохранить реквизиты; первое сохранение включает режим Business.</item>
/// <item><c>POST submit</c> — отправить на проверку (→ Pending).</item>
/// <item><c>POST disable</c> — вернуться в Private (Verified снимается).</item>
/// </list>
/// Verified/Rejected отсюда недостижимы: их выставляет только модератор
/// (<see cref="BusinessModerationController"/>). Правила — в <see cref="IBusinessAccountService"/>.
/// </summary>
[Authorize]
[Route("api/account/business")]
public class AccountBusinessController(
    IBusinessAccountService business,
    IValidator<SaveBusinessDetailsRequest> validator) : ApiControllerBase
{
    /// <summary>Текущий режим, реквизиты и статус проверки. Без реквизитов — Private и Details = null (не 404).</summary>
    [HttpGet]
    public async Task<ActionResult<BusinessAccountResponse>> Get(CancellationToken ct)
    {
        var profile = await business.FindAsync(CurrentUserId()!.Value, ct);
        return Ok(business.ToResponse(profile));
    }

    /// <summary>
    /// Сохранить/обновить реквизиты (и включить режим Business). 409 <c>details_locked</c>,
    /// пока заявка на проверке. Правка подтверждённых реквизитов снимает подтверждение —
    /// в ответе <c>verificationReset: true</c>.
    /// </summary>
    [HttpPut]
    public async Task<ActionResult<BusinessAccountResponse>> Save(SaveBusinessDetailsRequest request, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return this.ToProblem(validation);

        var result = await business.SaveAsync(CurrentUserId()!.Value, BusinessInput.ToDetails(request), ct);
        return result.Error is { } error
            ? this.ToProblem(error)
            : Ok(business.ToResponse(result.Profile, result.VerificationReset));
    }

    /// <summary>
    /// Отправить на проверку. Частота ограничена дважды: паузой после отказа по данным
    /// в БД (429 <c>resubmit_cooldown</c> с <c>retryAt</c>) и лимитом запросов в час
    /// на пользователя (политика <see cref="RateLimitPolicies.BusinessSubmit"/>).
    /// </summary>
    [HttpPost("submit")]
    [EnableRateLimiting(RateLimitPolicies.BusinessSubmit)]
    public async Task<ActionResult<BusinessAccountResponse>> Submit(CancellationToken ct)
    {
        var result = await business.SubmitAsync(CurrentUserId()!.Value, ct);
        return result.Error is { } error
            ? this.ToProblem(error)
            : Ok(business.ToResponse(result.Profile));
    }

    /// <summary>Вернуться в Private. Идемпотентно. Реквизиты сохраняются для повторного включения.</summary>
    [HttpPost("disable")]
    public async Task<ActionResult<BusinessAccountResponse>> Disable(CancellationToken ct)
    {
        var result = await business.DisableAsync(CurrentUserId()!.Value, ct);
        return result.Error is { } error
            ? this.ToProblem(error)
            : Ok(business.ToResponse(result.Profile));
    }
}
