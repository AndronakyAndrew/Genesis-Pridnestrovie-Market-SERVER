using System.Security.Claims;
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
    /// Id текущего пользователя из claim NameIdentifier.
    /// Возвращает null, если запрос неаутентифицирован.
    /// </summary>
    protected Guid? CurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
