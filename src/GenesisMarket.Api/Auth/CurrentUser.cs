using System.Security.Claims;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Auth;

/// <summary>
/// Текущий пользователь из claims JWT. ЕДИНСТВЕННАЯ точка получения текущего
/// пользователя во всём приложении — контроллеры и хендлеры авторизации
/// читают его отсюда, а не из HttpContext/claims напрямую.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    UserRole? Role { get; }
    bool IsAuthenticated { get; }
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirst("sub")?.Value, out var id) ? id : null;

    public UserRole? Role =>
        Enum.TryParse<UserRole>(Principal?.FindFirst("role")?.Value, out var role) ? role : null;
}
