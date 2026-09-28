using System.Buffers.Text;
using System.Text;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Общий журнал действий и история ролей — только владелец (<see cref="Permissions.AuditRead"/>).
/// Только чтение: ни одна ручка не меняет журнал, а БД запрещает это триггером.
/// </summary>
[Route("api/admin/audit")]
[Authorize(Policy = Permissions.AuditRead)]
public class AdminAuditController(AppDbContext db) : ApiControllerBase
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    /// <summary>Лента журнала, новые сверху. Курсорная пагинация по (OccurredAt, Id).</summary>
    [HttpGet]
    public async Task<ActionResult<AuditEventPage>> List([FromQuery] AuditQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);
        var q = db.AuditEvents.AsNoTracking().AsQueryable();

        if (query.ActorId is { } actor)
            q = q.Where(e => e.ActorId == actor);
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.Trim();
            if (action.EndsWith(".*", StringComparison.Ordinal))
            {
                var prefix = action[..^1];
                q = q.Where(e => e.Action.StartsWith(prefix));
            }
            else
                q = q.Where(e => e.Action == action);
        }
        if (!string.IsNullOrWhiteSpace(query.TargetType))
            q = q.Where(e => e.TargetType == query.TargetType.Trim());
        if (!string.IsNullOrWhiteSpace(query.TargetId))
            q = q.Where(e => e.TargetId == query.TargetId.Trim());
        if (query.From is { } from)
            q = q.Where(e => e.OccurredAt >= from);
        if (query.To is { } to)
            q = q.Where(e => e.OccurredAt < to);

        if (query.Cursor is { Length: > 0 } raw)
        {
            if (!TryDecode(raw, out var at, out var id))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);
            q = q.Where(e => e.OccurredAt < at || (e.OccurredAt == at && e.Id.CompareTo(id) < 0));
        }

        var rows = await q
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(limit + 1)
            .Select(e => new AuditEventItem(
                e.Id, e.OccurredAt, e.ActorId,
                db.Users.Where(u => u.Id == e.ActorId).Select(u => u.PublicCode).FirstOrDefault(),
                db.Profiles.Where(p => p.UserId == e.ActorId).Select(p => p.DisplayName).FirstOrDefault(),
                e.ActorKind, e.ActorLabel, e.Action, e.TargetType, e.TargetId,
                e.OldValue, e.NewValue, e.Reason, e.RequestId))
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        var next = hasMore ? Encode(page[^1].OccurredAt, page[^1].Id) : null;
        return Ok(new AuditEventPage(page, next));
    }

    /// <summary>История ролей: все изменения или по одному пользователю.</summary>
    [HttpGet("role-changes")]
    public async Task<ActionResult<IReadOnlyList<RoleChangeItem>>> RoleChanges(
        [FromQuery] Guid? userId, [FromQuery] int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var q = db.RoleChanges.AsNoTracking().AsQueryable();
        if (userId is { } uid)
            q = q.Where(c => c.UserId == uid);

        var items = await q
            .OrderByDescending(c => c.OccurredAt)
            .Take(take)
            .Select(c => new RoleChangeItem(
                c.Id, c.OccurredAt, c.UserId,
                db.Users.Where(u => u.Id == c.UserId).Select(u => u.PublicCode).FirstOrDefault(),
                c.OldRole, c.NewRole, c.ActorId,
                db.Users.Where(u => u.Id == c.ActorId).Select(u => u.PublicCode).FirstOrDefault(),
                c.ActorLabel, c.Source, c.Reason))
            .ToListAsync(ct);

        return Ok(items);
    }

    private static string Encode(DateTimeOffset at, Guid id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{at.UtcTicks}:{id:N}"));

    private static bool TryDecode(string raw, out DateTimeOffset at, out Guid id)
    {
        at = default;
        id = default;
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(raw)).Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParse(parts[1], out id))
                return false;
            at = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
