using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Auth.Roles;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Moderation;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Блокировка пользователей — уровень Admin (<see cref="Permissions.UsersBan"/>).
/// Санкции применяются только к роли строго ниже своей: админ не банит админа,
/// владельца не банит никто. Запись — в moderation_logs (рабочий журнал, по нему
/// живёт статистика банов) и зеркалом в audit_events со старым и новым состоянием.
/// </summary>
[Route("api/admin/users")]
[Authorize(Policy = Permissions.UsersBan)]
public class AdminUsersController(
    AppDbContext db,
    IModerationAudit audit,
    IRoleStore roles,
    IRefreshTokenService refreshTokens,
    SecurityStampValidator securityStamp,
    ILogger<AdminUsersController> logger) : ApiControllerBase
{
    // Статусы объявлений, снимаемые с публикации при бане владельца.
    private static readonly ListingStatus[] LiveStatuses =
        [ListingStatus.Active, ListingStatus.PendingReview];

    /// <summary>
    /// Забанить пользователя. В ОДНОЙ транзакции: IsBanned/BannedUntil, ротация
    /// SecurityStamp (иначе выданный access-токен жил бы ещё до 15 минут), архивация
    /// активных объявлений, отзыв всех refresh-токенов, запись в журнал. После
    /// коммита сбрасывается кэш снимка пользователя — бан действует немедленно.
    /// </summary>
    [HttpPost("{id:guid}/ban")]
    public async Task<ActionResult<ModerationActionResult>> Ban(
        Guid id, BanUserRequest request, CancellationToken ct)
    {
        if (id == CurrentUserId())
            return Problem(title: "Нельзя забанить самого себя", statusCode: StatusCodes.Status400BadRequest);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);
        if (await OutranksAsync(id, ct) is { } forbidden)
            return forbidden;

        var now = DateTimeOffset.UtcNow;
        var before = new { isBanned = user.IsBanned, bannedUntil = user.BannedUntil };

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        user.IsBanned = true;
        user.BannedUntil = request.Until;
        user.SecurityStamp = Guid.NewGuid();   // немедленно инвалидирует выданные access-токены
        user.UpdatedAt = now;

        // Активные объявления забаненного скрываем из каталога и убираем из очереди
        // модерации: разбирать публикации заблокированного автора незачем, а зависший
        // ReviewQueuedAt держал бы архивные объявления в очереди навсегда.
        var archived = await db.Listings
            .Where(l => l.OwnerId == id && LiveStatuses.Contains(l.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ListingStatus.Archived)
                .SetProperty(l => l.ReviewQueuedAt, (DateTimeOffset?)null)
                .SetProperty(l => l.ModerationPriority, 0)
                .SetProperty(l => l.UpdatedAt, now), ct);

        // Все refresh-токены отзываются (в этой же транзакции — общий DbContext).
        await refreshTokens.RevokeAllAsync(id, ct);

        audit.Record(ModerationLog.ActionBanUser, ModerationLog.TargetUser, id,
            reason: request.Reason,
            payload: new { isBanned = true, until = request.Until, reason = request.Reason, archivedListings = archived },
            before: before);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Снимок пользователя кэшируется в SecurityStampValidator — сбрасываем, чтобы бан подействовал сразу.
        securityStamp.Invalidate(id);

        logger.LogInformation("Пользователь {UserId} забанен {ActorId}. Архивировано объявлений: {Count}.",
            id, CurrentUserId(), archived);

        return Ok(new ModerationActionResult("Пользователь заблокирован."));
    }

    /// <summary>Снять бан: IsBanned = false, BannedUntil = null. Объявления не восстанавливаются автоматически.</summary>
    [HttpPost("{id:guid}/unban")]
    public async Task<ActionResult<ModerationActionResult>> Unban(Guid id, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);
        if (!user.IsBanned)
            return Problem(title: "Пользователь не заблокирован", statusCode: StatusCodes.Status409Conflict);

        var before = new { isBanned = user.IsBanned, bannedUntil = user.BannedUntil };

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        user.IsBanned = false;
        user.BannedUntil = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(ModerationLog.ActionUnbanUser, ModerationLog.TargetUser, id,
            payload: new { isBanned = false }, before: before);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Сбрасываем кэш снимка: иначе SecurityStampValidator до TTL считал бы пользователя забаненным.
        securityStamp.Invalidate(id);

        return Ok(new ModerationActionResult("Пользователь разблокирован."));
    }

    /// <summary>403, если цель не ниже исполнителя по роли; иначе null.</summary>
    private async Task<ObjectResult?> OutranksAsync(Guid targetId, CancellationToken ct)
    {
        var actor = CurrentUser.Role ?? UserRole.User;
        var target = await roles.GetRoleAsync(targetId, ct);
        return RolePermissions.Outranks(actor, target)
            ? null
            : Problem(title: "Нельзя заблокировать сотрудника того же или более высокого уровня",
                statusCode: StatusCodes.Status403Forbidden);
    }
}
