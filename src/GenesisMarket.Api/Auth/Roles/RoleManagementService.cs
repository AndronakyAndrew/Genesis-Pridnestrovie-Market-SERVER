using GenesisMarket.Api.Audit;
using GenesisMarket.Api.Security;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Auth.Roles;

public enum RoleChangeStatus
{
    Ok,
    NotFound,
    /// <summary>Роль не изменилась бы (цель уже в ней).</summary>
    NoChange,
    /// <summary>Роль цели изменилась с тех пор, как исполнитель её видел (подтверждение устарело).</summary>
    Stale,
    /// <summary>Назначение Owner через интерфейс.</summary>
    OwnerNotAssignable,
    /// <summary>Изменение собственной роли.</summary>
    Self,
    /// <summary>Изменение роли владельца.</summary>
    TargetIsOwner,
    /// <summary>Повышение заблокированного пользователя.</summary>
    TargetBanned,
    /// <summary>Owner уже есть, а передача не запрошена (CLI).</summary>
    OwnerExists
}

public sealed record RoleChangeResult(RoleChangeStatus Status, UserRole? OldRole = null, UserRole? NewRole = null);

/// <summary>
/// Смена роли — одно место для интерфейса и CLI. В одной транзакции: назначение
/// роли, ротация SecurityStamp и отзыв всех refresh-токенов цели (старый токен с
/// прежней ролью умирает сразу, новая роль попадает в токен только через новый вход
/// с 2FA), запись в <c>audit_events</c> и в <c>role_changes</c>.
/// </summary>
public sealed class RoleManagementService(
    AppDbContext db,
    IRoleStore roles,
    IAuditLog auditLog,
    IRefreshTokenService refreshTokens,
    SecurityStampValidator securityStamp,
    ISecurityAudit securityAudit,
    ILogger<RoleManagementService> logger)
{
    /// <summary>
    /// Смена роли из интерфейса владельцем. Правила: Owner не назначается и не
    /// снимается, свою роль менять нельзя, заблокированного не повышают.
    /// <paramref name="expectedRole"/> — роль, которую исполнитель видел в диалоге
    /// подтверждения: если за это время её поменяли, изменение не применяется.
    /// </summary>
    public async Task<RoleChangeResult> ChangeByOwnerAsync(
        Guid actorId, Guid targetUserId, UserRole newRole, UserRole expectedRole, string? reason,
        CancellationToken ct)
    {
        if (newRole == UserRole.Owner)
            return new(RoleChangeStatus.OwnerNotAssignable);
        if (targetUserId == actorId)
            return new(RoleChangeStatus.Self);

        var target = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId && !u.IsDeleted, ct);
        if (target is null)
            return new(RoleChangeStatus.NotFound);

        var current = await roles.GetRoleAsync(targetUserId, ct);
        if (current == UserRole.Owner)
            return new(RoleChangeStatus.TargetIsOwner);
        if (current != expectedRole)
            return new(RoleChangeStatus.Stale, current);
        if (current == newRole)
            return new(RoleChangeStatus.NoChange, current);
        if (newRole > current && IsBanned(target))
            return new(RoleChangeStatus.TargetBanned, current);

        await ApplyAsync(target, current, newRole, actorId, RoleChange.SourceUi, reason, demoteOwnerId: null, ct);
        return new(RoleChangeStatus.Ok, current, newRole);
    }

    /// <summary>
    /// Аварийная смена роли из консоли (<c>grant-role</c>). Может назначить Owner; если
    /// владелец уже есть, передача требует явного <paramref name="transferOwnership"/> —
    /// прежний владелец становится Admin в той же транзакции.
    /// </summary>
    public async Task<RoleChangeResult> ChangeFromCliAsync(
        string email, UserRole newRole, bool transferOwnership, string? reason, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var target = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized && !u.IsDeleted, ct);
        if (target is null)
            return new(RoleChangeStatus.NotFound);

        var current = await roles.GetRoleAsync(target.Id, ct);
        if (current == newRole)
            return new(RoleChangeStatus.NoChange, current);

        Guid? demoteOwner = null;
        if (newRole == UserRole.Owner && await roles.GetOwnerAsync(ct) is { } owner && owner != target.Id)
        {
            if (!transferOwnership)
                return new(RoleChangeStatus.OwnerExists, current);
            demoteOwner = owner;
        }

        await ApplyAsync(target, current, newRole, actorId: null, RoleChange.SourceCli, reason, demoteOwner, ct);
        return new(RoleChangeStatus.Ok, current, newRole);
    }

    private async Task ApplyAsync(
        User target, UserRole oldRole, UserRole newRole, Guid? actorId, string source, string? reason,
        Guid? demoteOwnerId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Передача владения: прежний владелец — сначала в Admin, иначе частичный
        // уникальный индекс «один Owner на проект» не пропустит нового.
        User? previousOwner = null;
        if (demoteOwnerId is { } previousId)
        {
            previousOwner = await db.Users.FirstAsync(u => u.Id == previousId, ct);
            await roles.SetRoleAsync(previousId, UserRole.Admin, actorId, ct);
            Record(previousOwner, UserRole.Owner, UserRole.Admin, actorId, source, reason ?? "Передача владения");
            await refreshTokens.RevokeAllAsync(previousId, ct);
            await db.SaveChangesAsync(ct);
        }

        await roles.SetRoleAsync(target.Id, newRole, actorId, ct);
        Record(target, oldRole, newRole, actorId, source, reason);
        // ExecuteUpdate на том же соединении — внутри этой же транзакции.
        await refreshTokens.RevokeAllAsync(target.Id, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        securityStamp.Invalidate(target.Id);
        if (previousOwner is not null)
            securityStamp.Invalidate(previousOwner.Id);

        securityAudit.RoleChanged(actorId ?? Guid.Empty, target.Id, oldRole.ToString(), newRole.ToString());
        logger.LogWarning("Роль пользователя {UserId} изменена: {OldRole} → {NewRole} ({Source}).",
            target.Id, oldRole, newRole, source);
    }

    /// <summary>
    /// Журнал и принудительный выход цели. Сессии отзываются при любом изменении: при
    /// понижении — чтобы права пропали сразу, при повышении — чтобы новая роль пришла
    /// только через вход с 2FA и короткой служебной сессией.
    /// </summary>
    private void Record(User target, UserRole oldRole, UserRole newRole, Guid? actorId, string source, string? reason)
    {
        target.SecurityStamp = Guid.NewGuid();
        target.UpdatedAt = DateTimeOffset.UtcNow;

        var audit = auditLog.Record(AuditActions.RoleChange, AuditActions.TargetUser, target.Id.ToString(),
            oldValue: new { role = oldRole.ToString() },
            newValue: new { role = newRole.ToString() },
            reason: reason);

        db.RoleChanges.Add(new RoleChange
        {
            ProjectKey = roles.ProjectKey,
            UserId = target.Id,
            OldRole = oldRole.ToString(),
            NewRole = newRole.ToString(),
            ActorId = actorId,
            Source = source,
            ActorLabel = audit.ActorLabel,
            Reason = audit.Reason,
            AuditEventId = audit.Id
        });
    }

    private static bool IsBanned(User u) =>
        u.IsBanned && (u.BannedUntil is null || u.BannedUntil > DateTimeOffset.UtcNow);
}
