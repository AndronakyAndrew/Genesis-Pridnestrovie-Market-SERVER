using Microsoft.AspNetCore.Mvc;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// База для контроллеров: доступ к идентификатору текущего пользователя из JWT.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>
    /// Id текущего пользователя из claim <c>sub</c>.
    /// Возвращает null, если запрос неаутентифицирован.
    /// </summary>
    protected Guid? CurrentUserId()
    {
        var raw = User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

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
