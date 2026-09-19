using System.Text.Json;
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
/// Журнал действий модерации — чтение <c>moderation_logs</c>. Таблица только на
/// добавление, здесь её только читают: ни правки, ни удаления записей API не даёт.
/// Сам просмотр журнала не журналируется — в нём нет контактов пользователей.
/// </summary>
[Route("api/moderation/logs")]
[Authorize(Policy = "Moderator")]
public class ModerationJournalController(AppDbContext db) : ApiControllerBase
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    /// <summary>Лента журнала, новые сверху. Фильтры — период, модератор, коды действий, тип объекта, поиск.</summary>
    [HttpGet]
    public async Task<ActionResult<ModerationLogPage>> List([FromQuery] ModerationLogQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        var q = ByPeriod(db.ModerationLogs.AsNoTracking(), query.From, query.To, query.ActorId);

        if (query.Action is { Length: > 0 } actions)
            q = q.Where(m => actions.Contains(m.Action));
        if (!string.IsNullOrWhiteSpace(query.TargetType))
            q = q.Where(m => m.TargetType == query.TargetType);

        q = await SearchAsync(q, query.Q, ct);

        if (query.Cursor is { Length: > 0 } raw)
        {
            if (!ModerationCursor.TryDecode(raw, out _, out var at, out var afterId))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);
            q = q.Where(m => m.CreatedAt < at || (m.CreatedAt == at && m.Id < afterId));
        }

        var rows = await q
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();

        var actors = await ModerationReadModels.LoadActorsAsync(db, page.Select(m => m.ActorId), ct);
        var targets = await ModerationReadModels.LoadTargetsAsync(
            db, page.Select(m => (m.TargetType, m.TargetId)), ct);

        var items = page.Select(m => new ModerationLogItem(
            m.Id,
            m.CreatedAt,
            actors.GetValueOrDefault(m.ActorId) ?? ModerationReadModels.UnknownActor(m.ActorId),
            m.Action,
            m.TargetType,
            m.TargetId,
            targets.GetValueOrDefault((m.TargetType, m.TargetId)),
            m.Reason,
            ParsePayload(m.PayloadJson),
            m.WaitSeconds)).ToList();

        var next = hasMore
            ? ModerationCursor.Encode(0, page[^1].CreatedAt, page[^1].Id)
            : null;

        return Ok(new ModerationLogPage(items, next, hasMore));
    }

    /// <summary>Сводка за период: плитки журнала и счётчики вкладок («Сегодня 56», «7 дней 342»).</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ModerationLogSummary>> Summary(
        [FromQuery] ModerationLogSummaryQuery query, CancellationToken ct)
    {
        var q = ByPeriod(db.ModerationLogs.AsNoTracking(), query.From, query.To, query.ActorId);

        var byAction = await q
            .GroupBy(m => m.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Action, x => x.Count, ct);

        var avgWait = await q
            .Where(m => m.WaitSeconds != null)
            .AverageAsync(m => (double?)m.WaitSeconds, ct);

        int Count(params string[] codes) => codes.Sum(c => byAction.GetValueOrDefault(c));

        return Ok(new ModerationLogSummary(
            Total: byAction.Values.Sum(),
            Approvals: Count(ModerationLog.ActionApproveListing),
            Rejections: Count(ModerationLog.ActionRejectListing),
            Revisions: Count(ModerationLog.ActionReviseListing),
            Bans: Count(ModerationLog.ActionBanUser),
            Warnings: Count(ModerationLog.ActionWarnUser),
            ReportsResolved: Count(ModerationLog.ActionResolveReport),
            BusinessDecisions: Count(ModerationLog.ActionApproveBusiness, ModerationLog.ActionRejectBusiness),
            AvgWaitSeconds: avgWait is { } a ? Math.Round(a, 1) : null));
    }

    /// <summary>Модераторы и администраторы — для фильтра «исполнитель».</summary>
    [HttpGet("actors")]
    public async Task<ActionResult<IReadOnlyList<ModerationActor>>> Actors(CancellationToken ct)
    {
        var actors = await db.Users.AsNoTracking()
            .Where(u => u.Role != UserRole.User && !u.IsDeleted)
            .OrderBy(u => u.Profile!.DisplayName)
            .Select(u => new ModerationActor(u.Id, u.PublicCode, u.Profile!.DisplayName, u.Role))
            .ToListAsync(ct);
        return Ok(actors);
    }

    // ---- helpers ----

    private static IQueryable<ModerationLog> ByPeriod(
        IQueryable<ModerationLog> q, DateTimeOffset? from, DateTimeOffset? to, Guid? actorId)
    {
        if (from is { } f)
            q = q.Where(m => m.CreatedAt >= f);
        if (to is { } t)
            q = q.Where(m => m.CreatedAt < t);
        if (actorId is { } a)
            q = q.Where(m => m.ActorId == a);
        return q;
    }

    /// <summary>
    /// Поиск: GUID — объект или исполнитель; «ID профиля» — действия над пользователем
    /// или им самим; иначе подстрока причины либо заголовка объявления-объекта.
    /// </summary>
    private async Task<IQueryable<ModerationLog>> SearchAsync(
        IQueryable<ModerationLog> q, string? raw, CancellationToken ct)
    {
        var search = ModerationSearch.Normalize(raw);
        if (search is null)
            return q;

        if (Guid.TryParse(search, out var id))
            return q.Where(m => m.TargetId == id || m.ActorId == id);

        if (ModerationSearch.TryPublicCode(search, out var code))
        {
            var userId = await db.Users.AsNoTracking()
                .Where(u => u.PublicCode == code)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(ct);
            return userId is { } uid
                ? q.Where(m => m.TargetId == uid || m.ActorId == uid)
                : q.Where(_ => false);
        }

        var pattern = ModerationSearch.LikePattern(search);
        return q.Where(m =>
            (m.Reason != null && EF.Functions.ILike(m.Reason, pattern, ModerationSearch.LikeEscape)) ||
            (m.TargetType == ModerationLog.TargetListing &&
             db.Listings.Any(l => l.Id == m.TargetId &&
                                  EF.Functions.ILike(l.Title, pattern, ModerationSearch.LikeEscape))));
    }

    private static JsonElement? ParsePayload(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Битый снимок не должен ронять журнал — строка покажется без payload.
            return null;
        }
    }
}
