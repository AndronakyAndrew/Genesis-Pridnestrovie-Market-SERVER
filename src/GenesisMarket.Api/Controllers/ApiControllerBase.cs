using GenesisMarket.Api.Auth;
using Microsoft.AspNetCore.Mvc;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// База для контроллеров: доступ к текущему пользователю (через ICurrentUser).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>Текущий пользователь — единственная точка чтения из claims.</summary>
    protected ICurrentUser CurrentUser =>
        HttpContext.RequestServices.GetRequiredService<ICurrentUser>();

    /// <summary>Id текущего пользователя. null, если запрос неаутентифицирован.</summary>
    protected Guid? CurrentUserId() => CurrentUser.UserId;

    /// <summary>IP клиента (для rate-limit и хеширования). "unknown", если недоступен.</summary>
    protected string ClientIp() =>
        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>429 с заголовком Retry-After.</summary>
    protected ObjectResult TooManyRequests(int retryAfterSeconds, string title = "Слишком много запросов, попробуйте позже")
    {
        Response.Headers.RetryAfter = Math.Max(retryAfterSeconds, 1).ToString();
        return Problem(title: title, statusCode: StatusCodes.Status429TooManyRequests);
    }
}
