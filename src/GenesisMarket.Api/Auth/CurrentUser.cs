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

    /// <summary>
    /// Сессия, которой принадлежит текущий запрос (claim sid). По ней список
    /// сессий помечает «это устройство» — не по времени последней активности.
    /// null — токен выпущен до появления claim'а.
    /// </summary>
    Guid? SessionId { get; }

    /// <summary>В этой сессии пройден второй фактор (claim <c>mfa</c>).</summary>
    bool MfaVerified { get; }

    /// <summary>
    /// Действует ли разрешение служебной зоны. Роль даёт разрешение, только если сессия
    /// прошла 2FA: сотрудник без второго фактора — обычный пользователь везде, в том
    /// числе вне /api/moderation и /api/admin (видимость неопубликованного, удаление
    /// чужих объявлений).
    /// </summary>
    bool Can(string permission);
}

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirst("sub")?.Value, out var id) ? id : null;

    public UserRole? Role =>
        Enum.TryParse<UserRole>(Principal?.FindFirst("role")?.Value, out var role) ? role : null;

    public Guid? SessionId =>
        Guid.TryParse(Principal?.FindFirst("sid")?.Value, out var id) ? id : null;

    public bool MfaVerified => MfaClaim.IsVerified(Principal);

    public bool Can(string permission) =>
        IsAuthenticated && MfaVerified && RolePermissions.Has(Role, permission);
}

/// <summary>Claim второго фактора: «1», если сессия прошла TOTP или резервный код.</summary>
public static class MfaClaim
{
    public const string Type = "mfa";
    public const string Verified = "1";

    public static bool IsVerified(ClaimsPrincipal? principal) =>
        principal?.FindFirst(Type)?.Value == Verified;
}
