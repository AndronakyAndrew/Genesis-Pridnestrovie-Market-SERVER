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
/// Реестр пользователей и досье для модератора. Контактов (email/телефон) здесь нет —
/// они по-прежнему отдаются только журналируемой ручкой <c>GET /api/moderation/users/{id}</c>.
/// Email в поиске не участвует по той же причине: иначе реестр стал бы нежурналируемым
/// резолвером «email → аккаунт».
/// </summary>
[Route("api/moderation/users")]
[Authorize(Policy = "Moderator")]
public class ModerationUsersController(AppDbContext db, IModerationAudit audit) : ApiControllerBase
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;

    /// <summary>Сколько последних санкций и объявлений показывать в досье.</summary>
    private const int DossierHistory = 20;
    private const int DossierListings = 8;
    private const int DossierLinkedAccounts = 10;

    [HttpGet]
    public async Task<ActionResult<ModerationUsersPage>> List([FromQuery] ModerationUsersQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);
        var now = DateTimeOffset.UtcNow;

        var users = db.Users.AsNoTracking().Where(u => !u.IsDeleted);

        switch (query.Tab ?? ModerationUsersTab.All)
        {
            case ModerationUsersTab.Banned:
                users = users.Where(u => u.IsBanned);
                break;
            case ModerationUsersTab.New:
                var since = now.AddHours(-48);
                users = users.Where(u => u.CreatedAt >= since);
                break;
            case ModerationUsersTab.Staff:
                users = users.Where(u => u.Role != UserRole.User);
                break;
            case ModerationUsersTab.Business:
                users = users.Where(u => u.BusinessProfile != null
                                         && u.BusinessProfile.AccountType == AccountType.Business
                                         && u.BusinessProfile.Status == BusinessVerificationStatus.Verified);
                break;
            case ModerationUsersTab.Reported:
                users = users.Where(u => db.Reports.Any(r =>
                    (r.Status == ReportStatus.New || r.Status == ReportStatus.InReview) &&
                    ((r.TargetType == ReportTargetType.User && r.TargetId == u.Id) ||
                     (r.TargetType == ReportTargetType.Listing &&
                      db.Listings.Any(l => l.Id == r.TargetId && l.OwnerId == u.Id)))));
                break;
            case ModerationUsersTab.Warned:
                users = users.Where(u => u.WarningsCount > 0);
                break;
        }

        if (query.City is { } city)
            users = users.Where(u => u.Profile!.City == city);

        if (ModerationSearch.Normalize(query.Q) is { } search)
        {
            if (ModerationSearch.TryPublicCode(search, out var code))
                users = users.Where(u => u.PublicCode == code);
            else if (search.StartsWith('@') && search.Length > 1)
            {
                var tg = search[1..];
                users = users.Where(u => u.Profile!.TelegramUsername != null &&
                                         EF.Functions.ILike(u.Profile.TelegramUsername, ModerationSearch.LikePattern(tg),
                                             ModerationSearch.LikeEscape));
            }
            else
            {
                var pattern = ModerationSearch.LikePattern(search);
                users = users.Where(u =>
                    EF.Functions.ILike(u.Profile!.DisplayName, pattern, ModerationSearch.LikeEscape) ||
                    (u.BusinessProfile != null &&
                     EF.Functions.ILike(u.BusinessProfile.ShopName, pattern, ModerationSearch.LikeEscape)));
            }
        }

        if (query.Cursor is { Length: > 0 } raw)
        {
            if (!ModerationCursor.TryDecode(raw, out _, out var at, out var afterId))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);
            users = users.Where(u => u.CreatedAt < at || (u.CreatedAt == at && u.Id < afterId));
        }

        var rows = await ModerationReadModels.ProjectUsers(db, users
                .OrderByDescending(u => u.CreatedAt)
                .ThenByDescending(u => u.Id)
                .Take(limit + 1))
            .ToListAsync(ct);

        // Порядок после проекции: подзапросы в Select не гарантируют сохранение сортировки.
        rows = rows.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).ToList();

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        var next = hasMore ? ModerationCursor.Encode(0, page[^1].CreatedAt, page[^1].Id) : null;

        return Ok(new ModerationUsersPage(page.Select(r => r.ToItem(Request)).ToList(), next, hasMore));
    }

    /// <summary>Плитки над реестром.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ModerationUsersSummary>> Summary(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var day = now.AddHours(-24);
        var twoDays = now.AddHours(-48);
        var users = db.Users.AsNoTracking().Where(u => !u.IsDeleted);

        var total = await users.CountAsync(ct);
        var new24 = await users.CountAsync(u => u.CreatedAt >= day, ct);
        var new48 = await users.CountAsync(u => u.CreatedAt >= twoDays, ct);
        var sellers = await users.CountAsync(
            u => db.Listings.Any(l => l.OwnerId == u.Id && l.Status == ListingStatus.Active), ct);
        var profileReports = await db.Reports.CountAsync(
            r => r.TargetType == ReportTargetType.User &&
                 (r.Status == ReportStatus.New || r.Status == ReportStatus.InReview), ct);

        // Бан «действует», пока не вышел срок. Истёкший, но не снятый флаг — не активный бан.
        var bans = users.Where(u => u.IsBanned && (u.BannedUntil == null || u.BannedUntil > now));
        var permanent = await bans.CountAsync(u => u.BannedUntil == null, ct);
        var temporary = await bans.CountAsync(u => u.BannedUntil != null, ct);

        return Ok(new ModerationUsersSummary(
            total, new24, new48, sellers, profileReports, permanent + temporary, temporary, permanent));
    }

    /// <summary>
    /// Досье пользователя: счётчики объявлений и жалоб, сетевой сигнал, история санкций.
    /// Без контактов — для них отдельная журналируемая ручка.
    /// </summary>
    [HttpGet("{id:guid}/dossier")]
    public async Task<ActionResult<ModerationUserDossier>> Dossier(Guid id, CancellationToken ct)
    {
        var user = await ModerationReadModels.LoadUserAsync(db, Request, id, ct);
        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        var profile = await db.Profiles.AsNoTracking()
            .Where(p => p.UserId == id)
            .Select(p => new { p.Description, p.TelegramUsername })
            .FirstOrDefaultAsync(ct);

        var byStatus = await db.Listings.AsNoTracking()
            .Where(l => l.OwnerId == id)
            .GroupBy(l => l.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status.ToString(), x => x.Count, ct);

        var recent = await db.Listings.AsNoTracking()
            .Where(l => l.OwnerId == id)
            .OrderByDescending(l => l.CreatedAt)
            .Take(DossierListings)
            .Select(l => new DossierListing(
                l.Id, l.Slug, l.Title, l.Status, l.Price, l.PriceType, l.CreatedAt,
                l.Images.OrderBy(i => i.SortOrder).Select(i => i.ThumbKey).FirstOrDefault(),
                l.RejectionReasonCode))
            .ToListAsync(ct);

        var filed = await db.Reports.AsNoTracking().CountAsync(r => r.ReporterId == id, ct);
        var filedRejected = await db.Reports.AsNoTracking()
            .CountAsync(r => r.ReporterId == id && r.Status == ReportStatus.Rejected, ct);
        var against = await db.Reports.AsNoTracking().CountAsync(r =>
            (r.TargetType == ReportTargetType.User && r.TargetId == id) ||
            (r.TargetType == ReportTargetType.Listing &&
             db.Listings.Any(l => l.Id == r.TargetId && l.OwnerId == id)), ct);

        var prefixes = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == id && t.IpPrefix != null)
            .GroupBy(t => t.IpPrefix!)
            .Select(g => new { Prefix = g.Key, Last = g.Max(t => t.CreatedAt) })
            .OrderByDescending(x => x.Last)
            .Take(5)
            .Select(x => x.Prefix)
            .ToListAsync(ct);

        var linkedIds = ModerationReadModels.LinkedUserIds(db, id);
        var linked = await db.Users.AsNoTracking()
            .Where(u => linkedIds.Contains(u.Id) && !u.IsDeleted)
            .OrderByDescending(u => u.CreatedAt)
            .Take(DossierLinkedAccounts)
            .Select(u => new LinkedAccount(u.Id, u.PublicCode, u.Profile!.DisplayName, u.IsBanned, u.CreatedAt))
            .ToListAsync(ct);

        string[] sanctionActions =
            [ModerationLog.ActionBanUser, ModerationLog.ActionUnbanUser, ModerationLog.ActionWarnUser];
        var logs = await db.ModerationLogs.AsNoTracking()
            .Where(m => m.TargetType == ModerationLog.TargetUser && m.TargetId == id &&
                        sanctionActions.Contains(m.Action))
            .OrderByDescending(m => m.CreatedAt)
            .Take(DossierHistory)
            .ToListAsync(ct);
        var actors = await ModerationReadModels.LoadActorsAsync(db, logs.Select(m => m.ActorId), ct);
        var sanctions = logs.Select(m => new SanctionHistoryItem(
            m.Id, m.Action, m.CreatedAt,
            actors.GetValueOrDefault(m.ActorId) ?? ModerationReadModels.UnknownActor(m.ActorId),
            m.Reason, UntilFromPayload(m.PayloadJson))).ToList();

        var businessStatus = await db.BusinessProfiles.AsNoTracking()
            .Where(b => b.UserId == id)
            .Select(b => (BusinessVerificationStatus?)b.Status)
            .FirstOrDefaultAsync(ct);

        return Ok(new ModerationUserDossier(
            user, profile?.Description, profile?.TelegramUsername, byStatus, recent,
            filed, filedRejected, against, prefixes, linked, sanctions, businessStatus));
    }

    /// <summary>
    /// Предупреждение — санкция без бана: счётчик на пользователе и запись в журнал с
    /// причиной. Доступ к аккаунту не меняется. Те же ограничения, что у бана: не себя
    /// и не администратора.
    /// </summary>
    [HttpPost("{id:guid}/warn")]
    public async Task<ActionResult<ModerationActionResult>> Warn(Guid id, WarnUserRequest request, CancellationToken ct)
    {
        if (id == CurrentUserId())
            return Problem(title: "Нельзя вынести предупреждение самому себе", statusCode: StatusCodes.Status400BadRequest);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);
        if (user.Role == UserRole.Admin)
            return Problem(title: "Нельзя вынести предупреждение администратору", statusCode: StatusCodes.Status403Forbidden);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        user.Warn(DateTimeOffset.UtcNow);
        audit.Record(ModerationLog.ActionWarnUser, ModerationLog.TargetUser, id,
            reason: request.Reason,
            payload: new { reason = request.Reason.Trim(), warningsCount = user.WarningsCount });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(new ModerationActionResult($"Предупреждение вынесено (всего: {user.WarningsCount})."));
    }

    /// <summary>Срок бана из снимка решения (<c>{"until": ...}</c>), если он там есть.</summary>
    private static DateTimeOffset? UntilFromPayload(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("until", out var until) &&
                   until.ValueKind == JsonValueKind.String &&
                   until.TryGetDateTimeOffset(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
