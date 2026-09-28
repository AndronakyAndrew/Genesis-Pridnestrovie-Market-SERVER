using GenesisMarket.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace GenesisMarket.Api.Auth;

/// <summary>Требование разрешения служебной зоны (<see cref="Permissions"/>).</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Разрешение действует, только если роль его даёт И сессия прошла второй фактор.
/// Сотрудник без 2FA получает 403 с кодом <see cref="MfaSetupRequiredCode"/> — по нему
/// фронтенд ведёт на настройку 2FA, а не показывает «нет прав».
/// </summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    public const string MfaSetupRequiredCode = "mfa_setup_required";

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var role = Enum.TryParse<UserRole>(context.User.FindFirst("role")?.Value, out var r) ? r : (UserRole?)null;

        if (!RolePermissions.Has(role, requirement.Permission))
            return Task.CompletedTask;

        if (!MfaClaim.IsVerified(context.User))
        {
            context.Fail(new AuthorizationFailureReason(this, MfaSetupRequiredCode));
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Превращает отказ «роль есть, 2FA нет» в ProblemDetails с машинным кодом. Остальные
/// исходы — стандартные (401 без тела, 403 без тела), как было до служебных ролей.
/// </summary>
public sealed class MfaAwareAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        var mfaMissing = result.Forbidden && result.AuthorizationFailure?.FailureReasons
            .Any(f => f.Message == PermissionHandler.MfaSetupRequiredCode) == true;

        if (!mfaMissing)
        {
            await _default.HandleAsync(next, context, policy, result);
            return;
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Для служебного раздела нужна двухфакторная аутентификация",
            Detail = "Включите 2FA в настройках безопасности и войдите заново."
        };
        problem.Extensions["code"] = PermissionHandler.MfaSetupRequiredCode;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null,
            "application/problem+json");
    }
}
