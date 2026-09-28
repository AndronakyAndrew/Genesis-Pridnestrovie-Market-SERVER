using GenesisMarket.Api.Audit;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Auth.Roles;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// «Сотрудники» — только владелец (<see cref="Permissions.RolesManage"/>). Список
/// сотрудников, поиск аккаунта по email и смена роли. Все правила смены — в
/// <see cref="RoleManagementService"/>, общем для интерфейса и CLI.
/// </summary>
[Route("api/admin/staff")]
[Authorize(Policy = Permissions.RolesManage)]
public class AdminStaffController(
    AppDbContext db,
    IRoleStore roles,
    RoleManagementService roleManagement,
    IAuditLog auditLog) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StaffMemberResponse>>> List(CancellationToken ct)
    {
        var project = roles.ProjectKey;
        var staff = await db.ProjectRoles.AsNoTracking()
            .Where(a => a.ProjectKey == project)
            .OrderByDescending(a => a.Role).ThenBy(a => a.GrantedAt)
            .Select(a => new StaffMemberResponse(
                a.UserId,
                a.User!.PublicCode,
                a.User.Profile!.DisplayName,
                a.User.Email,
                a.Role,
                a.User.TwoFactor != null && a.User.TwoFactor.EnabledAt != null,
                a.User.IsBanned,
                a.GrantedAt,
                a.GrantedByUserId,
                db.Users.Where(u => u.Id == a.GrantedByUserId).Select(u => u.PublicCode).FirstOrDefault()))
            .ToListAsync(ct);

        return Ok(staff);
    }

    /// <summary>
    /// Поиск аккаунта по точному email. Резолвер «email → аккаунт» есть только здесь, у
    /// владельца, и каждый вызов журналируется (найден или нет) — сам адрес в журнал не
    /// пишется.
    /// </summary>
    [HttpGet("lookup")]
    public async Task<ActionResult<StaffLookupResponse>> Lookup([FromQuery] string email, CancellationToken ct)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 256)
            return Problem(title: "Укажите email", statusCode: StatusCodes.Status400BadRequest);

        var project = roles.ProjectKey;
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Email == normalized && !u.IsDeleted)
            .Select(u => new StaffLookupResponse(
                u.Id,
                u.PublicCode,
                u.Profile!.DisplayName,
                u.Email,
                u.ProjectRoles.Where(r => r.ProjectKey == project).Select(r => (UserRole?)r.Role).FirstOrDefault()
                    ?? UserRole.User,
                u.TwoFactor != null && u.TwoFactor.EnabledAt != null,
                u.IsBanned,
                u.CreatedAt))
            .FirstOrDefaultAsync(ct);

        auditLog.Record(AuditActions.StaffLookupByEmail, AuditActions.TargetUser,
            user?.UserId.ToString() ?? "-", newValue: new { found = user is not null });
        await db.SaveChangesAsync(ct);

        return user is null
            ? Problem(title: "Пользователь с таким email не найден", statusCode: StatusCodes.Status404NotFound)
            : Ok(user);
    }

    /// <summary>Сменить роль. Подтверждение — на клиенте, сверка «ожидаемой» роли — здесь.</summary>
    [HttpPut("{userId:guid}/role")]
    public async Task<ActionResult<ChangeRoleResponse>> ChangeRole(
        Guid userId, ChangeRoleRequest request, CancellationToken ct)
    {
        var result = await roleManagement.ChangeByOwnerAsync(
            CurrentUserId()!.Value, userId, request.Role, request.ExpectedRole, request.Reason, ct);

        return result.Status switch
        {
            RoleChangeStatus.Ok => Ok(new ChangeRoleResponse(userId, result.OldRole!.Value, result.NewRole!.Value,
                RolePermissions.IsStaff(result.NewRole.Value)
                    ? "Роль изменена. Сессии сотрудника завершены: при входе потребуется 2FA."
                    : "Роль снята. Сессии пользователя завершены.")),
            RoleChangeStatus.NotFound => Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound),
            RoleChangeStatus.OwnerNotAssignable => Problem(
                title: "Роль владельца назначается только консольной командой", statusCode: StatusCodes.Status403Forbidden),
            RoleChangeStatus.Self => Problem(title: "Нельзя изменить собственную роль", statusCode: StatusCodes.Status403Forbidden),
            RoleChangeStatus.TargetIsOwner => Problem(title: "Роль владельца нельзя изменить", statusCode: StatusCodes.Status403Forbidden),
            RoleChangeStatus.TargetBanned => Problem(
                title: "Заблокированному пользователю нельзя выдать служебную роль", statusCode: StatusCodes.Status409Conflict),
            RoleChangeStatus.NoChange => Problem(title: "У пользователя уже эта роль", statusCode: StatusCodes.Status409Conflict),
            RoleChangeStatus.Stale => Problem(
                title: $"Роль пользователя уже изменилась (сейчас: {result.OldRole}). Обновите страницу.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }
}
