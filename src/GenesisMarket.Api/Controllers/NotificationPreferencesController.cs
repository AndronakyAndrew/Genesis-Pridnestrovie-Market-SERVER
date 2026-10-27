using GenesisMarket.Api.Accounts;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GenesisMarket.Api.Controllers;

[Authorize]
[Route("api/account/notification-preferences")]
public sealed class NotificationPreferencesController(IAccountNotificationPreferences preferences) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NotificationPreferencesResponse>> Get(CancellationToken ct)
    {
        var response = await preferences.GetAsync(CurrentUserId()!.Value, ct);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpPut]
    public async Task<ActionResult<NotificationPreferencesResponse>> Update(
        UpdateNotificationPreferencesRequest request, CancellationToken ct)
    {
        if (request.Groups is not null)
        {
            if (request.Groups.ContainsKey(NotificationGroups.Security))
                return ProblemWithCode("security_group_managed_by_server", "Настройки уведомлений безопасности нельзя изменить.");

            var unknown = request.Groups.Keys.FirstOrDefault(key => !NotificationGroups.IsKnown(key));
            if (unknown is not null)
                return ProblemWithCode("unknown_notification_group", "Неизвестная группа уведомлений.");
        }

        if (request.NotifyVia is { } channel && !Enum.IsDefined(channel))
            return ProblemWithCode("invalid_notification_channel", "Неизвестный канал уведомлений.");

        var response = await preferences.UpdateAsync(CurrentUserId()!.Value, request, ct);
        return response is null ? NotFound() : Ok(response);
    }

    private ObjectResult ProblemWithCode(string code, string title)
    {
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Title = title,
            Status = StatusCodes.Status400BadRequest
        };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
    }
}
