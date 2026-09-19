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
/// Экран жалоб: список с вкладками, карточка со снимком объекта и «взять в работу».
/// Закрытие жалобы — прежняя ручка <c>POST /api/moderation/reports/{id}/resolve</c>
/// (<see cref="ModerationController"/>): она же пишет итог в журнал.
/// </summary>
[Route("api/moderation/reports")]
[Authorize(Policy = "Moderator")]
public class ModerationReportsController(AppDbContext db, IModerationAudit audit) : ApiControllerBase
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;
    private const int ThumbsInSnapshot = 6;

    /// <summary>За сколько дней считается среднее время реакции.</summary>
    private const int ReactionWindowDays = 7;

    [HttpGet]
    public async Task<ActionResult<ModerationReportsPage>> List(
        [FromQuery] ModerationReportsQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);
        var sort = query.Sort ?? ModerationReportsSort.Urgent;

        var reports = db.Reports.AsNoTracking().AsQueryable();

        reports = (query.Tab ?? ModerationReportsTab.Open) switch
        {
            ModerationReportsTab.Open => reports.Where(r => r.Status == ReportStatus.New || r.Status == ReportStatus.InReview),
            ModerationReportsTab.New => reports.Where(r => r.Status == ReportStatus.New),
            ModerationReportsTab.InReview => reports.Where(r => r.Status == ReportStatus.InReview),
            ModerationReportsTab.Closed => reports.Where(r => r.Status == ReportStatus.Resolved || r.Status == ReportStatus.Rejected),
            _ => reports
        };

        if (query.TargetType is { } targetType)
            reports = reports.Where(r => r.TargetType == targetType);
        if (query.Reason is { } reason)
            reports = reports.Where(r => r.Reason == reason);
        if (query.Mine == true && CurrentUserId() is { } me)
            reports = reports.Where(r => r.AssignedToUserId == me);

        reports = await SearchAsync(reports, query.Q, ct);

        // Тяжесть — в проекции, чтобы по ней же сортировать и продолжать курсор.
        // Правило то же, что в ReportSeverity.Of (здесь — в виде, транслируемом в CASE).
        var ranked = reports.Select(r => new
        {
            Report = r,
            Severity = r.Reason == ReportReason.Fraud || r.Reason == ReportReason.Prohibited ? ReportSeverity.High
                : r.Reason == ReportReason.PriceViolation ? ReportSeverity.Medium
                : ReportSeverity.Low
        });

        if (query.Cursor is { Length: > 0 } raw)
        {
            if (!ModerationCursor.TryDecode(raw, out var sev, out var at, out var afterId))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);

            ranked = sort switch
            {
                ModerationReportsSort.Urgent => ranked.Where(x =>
                    x.Severity < sev ||
                    (x.Severity == sev && x.Report.CreatedAt > at) ||
                    (x.Severity == sev && x.Report.CreatedAt == at && x.Report.Id > afterId)),
                ModerationReportsSort.Oldest => ranked.Where(x =>
                    x.Report.CreatedAt > at || (x.Report.CreatedAt == at && x.Report.Id > afterId)),
                _ => ranked.Where(x =>
                    x.Report.CreatedAt < at || (x.Report.CreatedAt == at && x.Report.Id < afterId))
            };
        }

        ranked = sort switch
        {
            ModerationReportsSort.Urgent => ranked
                .OrderByDescending(x => x.Severity).ThenBy(x => x.Report.CreatedAt).ThenBy(x => x.Report.Id),
            ModerationReportsSort.Oldest => ranked
                .OrderBy(x => x.Report.CreatedAt).ThenBy(x => x.Report.Id),
            _ => ranked
                .OrderByDescending(x => x.Report.CreatedAt).ThenByDescending(x => x.Report.Id)
        };

        var rows = await ranked.Take(limit + 1).Select(x => x.Report).ToListAsync(ct);
        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();

        var items = await ToRowsAsync(page, ct);
        var last = page.LastOrDefault();
        var next = hasMore && last is not null
            ? ModerationCursor.Encode(ReportSeverity.Of(last.Reason), last.CreatedAt, last.Id)
            : null;

        return Ok(new ModerationReportsPage(items, next, hasMore));
    }

    /// <summary>Счётчики вкладок и среднее время реакции за 7 дней.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ModerationReportsSummary>> Summary(CancellationToken ct)
    {
        var byStatus = await db.Reports.AsNoTracking()
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);

        var openByTarget = await db.Reports.AsNoTracking()
            .Where(r => r.Status == ReportStatus.New || r.Status == ReportStatus.InReview)
            .GroupBy(r => r.TargetType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Type, x => x.Count, ct);

        // Среднее считаем в памяти: разность timestamptz → минуты переносима лучше,
        // чем трансляция TimeSpan в SQL, а закрытых за неделю немного.
        var since = DateTimeOffset.UtcNow.AddDays(-ReactionWindowDays);
        var closed = await db.Reports.AsNoTracking()
            .Where(r => r.ResolvedAt != null && r.ResolvedAt >= since)
            .Select(r => new { r.CreatedAt, ResolvedAt = r.ResolvedAt!.Value })
            .Take(5000)
            .ToListAsync(ct);
        double? avg = closed.Count == 0
            ? null
            : Math.Round(closed.Average(r => (r.ResolvedAt - r.CreatedAt).TotalMinutes), 1);

        int S(ReportStatus s) => byStatus.GetValueOrDefault(s);

        return Ok(new ModerationReportsSummary(
            All: byStatus.Values.Sum(),
            New: S(ReportStatus.New),
            InReview: S(ReportStatus.InReview),
            Closed: S(ReportStatus.Resolved) + S(ReportStatus.Rejected),
            OpenListings: openByTarget.GetValueOrDefault(ReportTargetType.Listing),
            OpenUsers: openByTarget.GetValueOrDefault(ReportTargetType.User),
            OpenReviews: openByTarget.GetValueOrDefault(ReportTargetType.Review),
            AvgReactionMinutes: avg));
    }

    /// <summary>
    /// Карточка жалобы: заявитель с его статистикой и снимок объекта. Текст объявления —
    /// сырой (без редактирования контактов): модератору нужен оригинал.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ModerationReportDetail>> Get(Guid id, CancellationToken ct)
    {
        var report = await db.Reports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null)
            return Problem(title: "Жалоба не найдена", statusCode: StatusCodes.Status404NotFound);

        var row = (await ToRowsAsync([report], ct))[0];

        ReporterStats? reporter = null;
        if (report.ReporterId is { } reporterId)
        {
            reporter = await db.Users.AsNoTracking()
                .Where(u => u.Id == reporterId)
                .Select(u => new ReporterStats(
                    u.Id, u.PublicCode, u.Profile!.DisplayName, u.CreatedAt, u.PhoneVerified,
                    db.Reports.Count(r => r.ReporterId == u.Id),
                    db.Reports.Count(r => r.ReporterId == u.Id && r.Status == ReportStatus.Resolved),
                    db.Reports.Count(r => r.ReporterId == u.Id && r.Status == ReportStatus.Rejected)))
                .FirstOrDefaultAsync(ct);
        }

        ReportedListing? listing = null;
        ModerationUserItem? user = null;
        ReportedReview? review = null;

        switch (report.TargetType)
        {
            case ReportTargetType.Listing:
                listing = await LoadListingAsync(report.TargetId, ct);
                break;
            case ReportTargetType.User:
                user = await ModerationReadModels.LoadUserAsync(db, Request, report.TargetId, ct);
                break;
            case ReportTargetType.Review:
                review = await db.Reviews.AsNoTracking()
                    .Where(r => r.Id == report.TargetId)
                    .Select(r => new ReportedReview(
                        r.Id, r.Rating, r.Text, r.IsHidden, r.CreatedAt, r.ListingId,
                        r.Author!.PublicCode, r.Author.Profile!.DisplayName,
                        db.Users.Where(u => u.Id == r.TargetUserId).Select(u => u.PublicCode).First()))
                    .FirstOrDefaultAsync(ct);
                break;
        }

        var others = await db.Reports.AsNoTracking().CountAsync(r =>
            r.Id != id && r.TargetType == report.TargetType && r.TargetId == report.TargetId &&
            (r.Status == ReportStatus.New || r.Status == ReportStatus.InReview), ct);

        return Ok(new ModerationReportDetail(row, reporter, listing, user, review, others));
    }

    /// <summary>
    /// Взять жалобу в работу: New → InReview и назначение на себя. Чужую жалобу в работе
    /// перехватить можно только явно (<c>?force=true</c>) — иначе 409, чтобы два
    /// модератора не разбирали одно и то же. Условный UPDATE: гонку двух «взять»
    /// выигрывает один.
    /// </summary>
    [HttpPost("{id:guid}/take")]
    public async Task<ActionResult<ModerationActionResult>> Take(Guid id, [FromQuery] bool force, CancellationToken ct)
    {
        var me = CurrentUserId()!.Value;
        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var updated = await db.Reports
            .Where(r => r.Id == id &&
                        (r.Status == ReportStatus.New ||
                         (r.Status == ReportStatus.InReview &&
                          (force || r.AssignedToUserId == null || r.AssignedToUserId == me))))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ReportStatus.InReview)
                .SetProperty(r => r.AssignedToUserId, (Guid?)me)
                .SetProperty(r => r.AssignedAt, (DateTimeOffset?)now)
                .SetProperty(r => r.UpdatedAt, (DateTimeOffset?)now), ct);

        if (updated == 0)
        {
            var state = await db.Reports.AsNoTracking()
                .Where(r => r.Id == id)
                .Select(r => new { r.Status })
                .FirstOrDefaultAsync(ct);

            if (state is null)
                return Problem(title: "Жалоба не найдена", statusCode: StatusCodes.Status404NotFound);
            if (state.Status is ReportStatus.Resolved or ReportStatus.Rejected)
                return Problem(title: "Жалоба уже закрыта", statusCode: StatusCodes.Status409Conflict);
            return Problem(title: "Жалобу уже разбирает другой модератор", statusCode: StatusCodes.Status409Conflict);
        }

        audit.Record(ModerationLog.ActionTakeReport, ModerationLog.TargetReport, id,
            payload: new { force });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(new ModerationActionResult("Жалоба взята в работу."));
    }

    // ---- helpers ----

    private async Task<IQueryable<Report>> SearchAsync(IQueryable<Report> q, string? raw, CancellationToken ct)
    {
        var search = ModerationSearch.Normalize(raw);
        if (search is null)
            return q;

        if (Guid.TryParse(search, out var id))
            return q.Where(r => r.Id == id || r.TargetId == id);

        if (ModerationSearch.TryPublicCode(search, out var code))
        {
            var userId = await db.Users.AsNoTracking()
                .Where(u => u.PublicCode == code)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(ct);
            return userId is { } uid
                ? q.Where(r => r.ReporterId == uid || (r.TargetType == ReportTargetType.User && r.TargetId == uid))
                : q.Where(_ => false);
        }

        var pattern = ModerationSearch.LikePattern(search);
        return q.Where(r =>
            (r.Comment != null && EF.Functions.ILike(r.Comment, pattern, ModerationSearch.LikeEscape)) ||
            (r.TargetType == ReportTargetType.Listing &&
             db.Listings.Any(l => l.Id == r.TargetId &&
                                  EF.Functions.ILike(l.Title, pattern, ModerationSearch.LikeEscape))));
    }

    /// <summary>Строки жалоб со ссылками на объект, заявителем и исполнителем — пачкой.</summary>
    private async Task<List<ModerationReportRow>> ToRowsAsync(List<Report> reports, CancellationToken ct)
    {
        var targets = await ModerationReadModels.LoadTargetsAsync(
            db, reports.Select(r => (ModerationReadModels.TargetTypeOf(r.TargetType), r.TargetId)), ct);

        var reporterIds = reports.Where(r => r.ReporterId != null).Select(r => r.ReporterId!.Value).Distinct().ToList();
        var reporters = reporterIds.Count == 0
            ? new Dictionary<Guid, ReporterRef>()
            : await db.Users.AsNoTracking()
                .Where(u => reporterIds.Contains(u.Id))
                .Select(u => new ReporterRef(u.Id, u.PublicCode, u.Profile!.DisplayName))
                .ToDictionaryAsync(u => u.Id, ct);

        var assignees = await ModerationReadModels.LoadActorsAsync(
            db, reports.Where(r => r.AssignedToUserId != null).Select(r => r.AssignedToUserId!.Value), ct);

        return reports.Select(r => new ModerationReportRow(
            r.Id, r.Reason, ReportSeverity.Of(r.Reason), r.Status, r.Comment, r.CreatedAt,
            r.TargetType, r.TargetId,
            targets.GetValueOrDefault((ModerationReadModels.TargetTypeOf(r.TargetType), r.TargetId)),
            r.ReporterId is { } rid ? reporters.GetValueOrDefault(rid) : null,
            r.AssignedToUserId is { } aid ? assignees.GetValueOrDefault(aid) : null,
            r.AssignedAt, r.ResolvedAt, r.Resolution)).ToList();
    }

    private async Task<ReportedListing?> LoadListingAsync(Guid listingId, CancellationToken ct)
    {
        var l = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == listingId)
            .Select(x => new
            {
                x.Id, x.Slug, x.Title, x.Description, x.Price, x.PriceType, x.Category, x.SubcategoryId,
                x.City, x.District, x.Status, x.CreatedAt, x.PublishedAt, x.ViewsCount, x.DeletedAt, x.OwnerId,
                Thumbs = x.Images.OrderBy(i => i.SortOrder).Take(ThumbsInSnapshot).Select(i => i.ThumbKey).ToList()
            })
            .FirstOrDefaultAsync(ct);
        if (l is null)
            return null;

        var owner = await ModerationReadModels.LoadUserAsync(db, Request, l.OwnerId, ct);
        if (owner is null)
            return null;

        return new ReportedListing(
            l.Id, l.Slug, l.Title, l.Description, l.Price, l.PriceType, l.Category, l.SubcategoryId,
            l.City, l.District, l.Status, l.CreatedAt, l.PublishedAt, l.ViewsCount, l.DeletedAt != null,
            l.Thumbs, owner);
    }
}
