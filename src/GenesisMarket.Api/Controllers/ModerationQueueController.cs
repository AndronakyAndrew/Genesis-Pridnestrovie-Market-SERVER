using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Api.Moderation;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Очередь объявлений как таблица: вкладки (на проверке / на доработке / отклонённые),
/// фильтры, счётчики, назначение на себя и пакетные решения. Одиночные решения —
/// прежние ручки <see cref="ModerationController"/>; пакет проходит через тот же
/// <see cref="IListingDecisions"/>, поэтому ничем от них не отличается.
/// </summary>
[Route("api/moderation/listings")]
[Authorize(Policy = "Moderator")]
public class ModerationQueueController(
    AppDbContext db,
    IListingDecisions decisions,
    IModerationAudit audit,
    IOptions<ModerationOptions> options) : ApiControllerBase
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 100;
    private const int MaxBulk = 50;

    [HttpGet]
    public async Task<ActionResult<ModerationListingsPage>> List(
        [FromQuery] ModerationListingsQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);
        var sort = query.Sort ?? ModerationListingsSort.Priority;
        var now = DateTimeOffset.UtcNow;
        var opts = options.Value;

        var listings = ByTab(query.Tab ?? ModerationListingsTab.Review, now);

        if (query.Category is { } category)
            listings = listings.Where(l => l.Category == category);
        if (query.City is { } city)
            listings = listings.Where(l => l.City == city);
        if (query.Mine == true && CurrentUserId() is { } me)
            listings = listings.Where(l => l.ReviewAssigneeId == me);

        listings = await SearchAsync(listings, query.Q, ct);

        // «Время в очереди» у каждой вкладки своё: постановка, возврат или отказ.
        var ranked = listings.Select(l => new
        {
            Listing = l,
            At = l.ReviewQueuedAt ?? l.RevisionRequestedAt ?? l.RejectedAt ?? l.CreatedAt,
            Priority = l.ModerationPriority
        });

        if (query.Cursor is { Length: > 0 } raw)
        {
            if (!ModerationCursor.TryDecode(raw, out var p, out var at, out var afterId))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);

            ranked = sort switch
            {
                ModerationListingsSort.Priority => ranked.Where(x =>
                    x.Priority < p ||
                    (x.Priority == p && x.At > at) ||
                    (x.Priority == p && x.At == at && x.Listing.Id > afterId)),
                ModerationListingsSort.Oldest => ranked.Where(x =>
                    x.At > at || (x.At == at && x.Listing.Id > afterId)),
                _ => ranked.Where(x =>
                    x.At < at || (x.At == at && x.Listing.Id < afterId))
            };
        }

        ranked = sort switch
        {
            ModerationListingsSort.Priority => ranked
                .OrderByDescending(x => x.Priority).ThenBy(x => x.At).ThenBy(x => x.Listing.Id),
            ModerationListingsSort.Oldest => ranked.OrderBy(x => x.At).ThenBy(x => x.Listing.Id),
            _ => ranked.OrderByDescending(x => x.At).ThenByDescending(x => x.Listing.Id)
        };

        var rows = await ranked
            .Take(limit + 1)
            .Select(x => new
            {
                x.Listing.Id, x.Listing.Slug, x.Listing.Title, x.Listing.Price, x.Listing.PriceType,
                x.Listing.Category, x.Listing.SubcategoryId, x.Listing.City, x.Listing.Status,
                x.Priority, x.At,
                x.Listing.ReviewQueuedAt, x.Listing.RevisionRequestedAt, x.Listing.RejectedAt,
                x.Listing.RejectionReasonCode, x.Listing.CreatedAt,
                Thumb = x.Listing.Images.OrderBy(i => i.SortOrder).Select(i => i.ThumbKey).FirstOrDefault(),
                ImagesCount = x.Listing.Images.Count,
                OpenReports = db.Reports.Count(r =>
                    r.TargetType == ReportTargetType.Listing && r.TargetId == x.Listing.Id &&
                    (r.Status == ReportStatus.New || r.Status == ReportStatus.InReview)),
                x.Listing.OwnerId,
                OwnerCode = x.Listing.Owner!.PublicCode,
                OwnerName = x.Listing.Owner.Profile!.DisplayName,
                OwnerCreatedAt = x.Listing.Owner.CreatedAt,
                OwnerPhoneVerified = x.Listing.Owner.PhoneVerified,
                OwnerApproved = x.Listing.Owner.ApprovedListingsCount,
                OwnerRejected = db.Listings.Count(o =>
                    o.OwnerId == x.Listing.OwnerId && o.Status == ListingStatus.Rejected),
                OwnerLastRejectedAt = x.Listing.Owner.LastRejectedAt,
                OwnerBusiness = x.Listing.Owner.BusinessProfile != null
                                && x.Listing.Owner.BusinessProfile.AccountType == AccountType.Business
                                && x.Listing.Owner.BusinessProfile.Status == BusinessVerificationStatus.Verified,
                x.Listing.ReviewAssigneeId, x.Listing.ReviewAssignedAt
            })
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();

        var assignees = await ModerationReadModels.LoadActorsAsync(
            db, page.Where(r => r.ReviewAssigneeId != null).Select(r => r.ReviewAssigneeId!.Value), ct);
        var overdueBefore = now.AddMinutes(-opts.ReviewSlaMinutes);

        var items = page.Select(r => new ModerationListingRow(
            r.Id, r.Slug, r.Title, r.Price, r.PriceType, r.Category, r.SubcategoryId, r.City, r.Status,
            r.Priority, r.At, r.ReviewQueuedAt, r.RevisionRequestedAt, r.RejectedAt, r.RejectionReasonCode,
            r.CreatedAt, r.Thumb, r.ImagesCount, r.OpenReports,
            new QueueOwner(r.OwnerId, r.OwnerCode, r.OwnerName, r.OwnerCreatedAt, r.OwnerPhoneVerified,
                r.OwnerApproved, r.OwnerRejected, r.OwnerLastRejectedAt, r.OwnerBusiness),
            r.ReviewAssigneeId is { } aid ? assignees.GetValueOrDefault(aid) : null,
            r.ReviewAssignedAt,
            Overdue: r.ReviewQueuedAt is { } q && q < overdueBefore)).ToList();

        var last = page.LastOrDefault();
        var next = hasMore && last is not null
            ? ModerationCursor.Encode(sort == ModerationListingsSort.Priority ? last.Priority : 0, last.At, last.Id)
            : null;

        return Ok(new ModerationListingsPage(items, next, hasMore));
    }

    /// <summary>Счётчики вкладок очереди, «мои», просроченные и самое старое ожидание.</summary>
    [HttpGet("counts")]
    public async Task<ActionResult<ModerationListingsCounts>> Counts(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var opts = options.Value;
        var me = CurrentUserId();
        var overdueBefore = now.AddMinutes(-opts.ReviewSlaMinutes);

        var review = await ByTab(ModerationListingsTab.Review, now).CountAsync(ct);
        var revision = await ByTab(ModerationListingsTab.Revision, now).CountAsync(ct);
        var rejected = await ByTab(ModerationListingsTab.Rejected, now).CountAsync(ct);
        var mine = await db.Listings.CountAsync(l => l.ReviewQueuedAt != null && l.ReviewAssigneeId == me, ct);
        var overdue = await db.Listings.CountAsync(l => l.ReviewQueuedAt != null && l.ReviewQueuedAt < overdueBefore, ct);
        var oldest = await db.Listings.Where(l => l.ReviewQueuedAt != null).MinAsync(l => l.ReviewQueuedAt, ct);

        return Ok(new ModerationListingsCounts(
            review, revision, rejected, review + revision + rejected, mine, overdue, oldest, opts.ReviewSlaMinutes));
    }

    /// <summary>
    /// Назначить объявление на себя. Чужое назначение перехватывается только явно
    /// (<c>?force=true</c>) — иначе 409, как у жалоб.
    /// </summary>
    [HttpPost("{id:guid}/assign")]
    public async Task<ActionResult<ModerationActionResult>> Assign(Guid id, [FromQuery] bool force, CancellationToken ct)
    {
        var me = CurrentUserId()!.Value;
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id, ct);

        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);
        if (listing.ReviewQueuedAt is null)
            return Problem(title: "Объявление не находится на модерации", statusCode: StatusCodes.Status409Conflict);
        if (listing.ReviewAssigneeId is { } other && other != me && !force)
            return Problem(title: "Объявление уже назначено другому модератору", statusCode: StatusCodes.Status409Conflict);

        listing.AssignReview(me, DateTimeOffset.UtcNow);
        audit.Record(ModerationLog.ActionAssignListing, ModerationLog.TargetListing, id, payload: new { force });
        await db.SaveChangesAsync(ct);

        return Ok(new ModerationActionResult("Объявление назначено на вас."));
    }

    /// <summary>Снять назначение (своё или, с правом модератора, чужое) — вернуть в общую очередь.</summary>
    [HttpPost("{id:guid}/unassign")]
    public async Task<ActionResult<ModerationActionResult>> Unassign(Guid id, CancellationToken ct)
    {
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);
        if (listing.ReviewAssigneeId is null)
            return Ok(new ModerationActionResult("Объявление ни на кого не назначено."));

        listing.UnassignReview();
        audit.Record(ModerationLog.ActionUnassignListing, ModerationLog.TargetListing, id);
        await db.SaveChangesAsync(ct);

        return Ok(new ModerationActionResult("Назначение снято."));
    }

    /// <summary>Назначить пачку на себя. Чужие назначения не перехватываются — они уходят в Failed.</summary>
    [HttpPost("bulk/assign")]
    public async Task<ActionResult<BulkResult>> BulkAssign(BulkListingsRequest request, CancellationToken ct)
    {
        var me = CurrentUserId()!.Value;
        var now = DateTimeOffset.UtcNow;

        return await BulkAsync(request.Ids, ct, (listing, _) =>
        {
            if (listing.ReviewAssigneeId is { } other && other != me)
                return Task.FromResult<string?>("Назначено другому модератору");

            listing.AssignReview(me, now);
            audit.Record(ModerationLog.ActionAssignListing, ModerationLog.TargetListing, listing.Id,
                payload: new { bulk = true });
            return Task.FromResult<string?>(null);
        });
    }

    /// <summary>Одобрить пачку. Не стоящие в очереди — в Failed, остальные — одной транзакцией.</summary>
    [HttpPost("bulk/approve")]
    public Task<ActionResult<BulkResult>> BulkApprove(BulkListingsRequest request, CancellationToken ct) =>
        BulkAsync(request.Ids, ct, async (listing, token) =>
        {
            await decisions.ApplyAsync(listing, ListingDecision.Approve, null, null, token);
            return null;
        });

    /// <summary>Отклонить пачку с одной причиной.</summary>
    [HttpPost("bulk/reject")]
    public Task<ActionResult<BulkResult>> BulkReject(BulkRejectRequest request, CancellationToken ct) =>
        BulkDecisionAsync(request, ListingDecision.Reject, ct);

    /// <summary>Вернуть пачку на доработку с одной причиной.</summary>
    [HttpPost("bulk/revise")]
    public Task<ActionResult<BulkResult>> BulkRevise(BulkRejectRequest request, CancellationToken ct) =>
        BulkDecisionAsync(request, ListingDecision.Revise, ct);

    // ---- helpers ----

    private async Task<ActionResult<BulkResult>> BulkDecisionAsync(
        BulkRejectRequest request, ListingDecision decision, CancellationToken ct)
    {
        if (IListingDecisions.ValidateReason(decision, request.Reason, request.Comment) is { } error)
            return Problem(title: error, statusCode: StatusCodes.Status400BadRequest);

        return await BulkAsync(request.Ids, ct, async (listing, token) =>
        {
            await decisions.ApplyAsync(listing, decision, request.Reason, request.Comment, token);
            return null;
        });
    }

    /// <summary>
    /// Общий каркас пакета: загрузить объявления, отсеять ненайденные и не стоящие в
    /// очереди, применить действие к остальным и закоммитить всё одной транзакцией
    /// вместе с журналом. Действие возвращает текст ошибки, чтобы пропустить элемент.
    /// </summary>
    private async Task<ActionResult<BulkResult>> BulkAsync(
        IReadOnlyList<Guid> rawIds, CancellationToken ct, Func<Listing, CancellationToken, Task<string?>> action)
    {
        var ids = rawIds.Distinct().ToList();
        if (ids.Count == 0)
            return Problem(title: "Не выбрано ни одного объявления", statusCode: StatusCodes.Status400BadRequest);
        if (ids.Count > MaxBulk)
            return Problem(title: $"За раз — не больше {MaxBulk} объявлений", statusCode: StatusCodes.Status400BadRequest);

        var listings = await db.Listings.IgnoreQueryFilters()
            .Where(l => ids.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);

        var succeeded = new List<Guid>();
        var failed = new List<BulkFailure>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var id in ids)
        {
            if (!listings.TryGetValue(id, out var listing))
            {
                failed.Add(new BulkFailure(id, "Объявление не найдено"));
                continue;
            }
            if (listing.ReviewQueuedAt is null)
            {
                failed.Add(new BulkFailure(id, "Объявление не находится на модерации"));
                continue;
            }

            if (await action(listing, ct) is { } error)
                failed.Add(new BulkFailure(id, error));
            else
                succeeded.Add(id);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(new BulkResult(succeeded, failed));
    }

    private IQueryable<Listing> ByTab(ModerationListingsTab tab, DateTimeOffset now)
    {
        var rejectedSince = now.AddDays(-options.Value.RejectedTabDays);
        var q = db.Listings.AsNoTracking();

        return tab switch
        {
            ModerationListingsTab.Review => q.Where(l => l.ReviewQueuedAt != null),
            ModerationListingsTab.Revision => q.Where(l =>
                l.RevisionRequestedAt != null && l.Status == ListingStatus.Draft),
            ModerationListingsTab.Rejected => q.Where(l =>
                l.Status == ListingStatus.Rejected && l.RejectedAt >= rejectedSince),
            _ => q.Where(l =>
                l.ReviewQueuedAt != null ||
                (l.RevisionRequestedAt != null && l.Status == ListingStatus.Draft) ||
                (l.Status == ListingStatus.Rejected && l.RejectedAt >= rejectedSince))
        };
    }

    private async Task<IQueryable<Listing>> SearchAsync(IQueryable<Listing> q, string? raw, CancellationToken ct)
    {
        var search = ModerationSearch.Normalize(raw);
        if (search is null)
            return q;

        if (Guid.TryParse(search, out var id))
            return q.Where(l => l.Id == id);

        if (ModerationSearch.TryPublicCode(search, out var code))
        {
            var ownerId = await db.Users.AsNoTracking()
                .Where(u => u.PublicCode == code)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(ct);
            return ownerId is { } oid ? q.Where(l => l.OwnerId == oid) : q.Where(_ => false);
        }

        var pattern = ModerationSearch.LikePattern(search);
        return q.Where(l => EF.Functions.ILike(l.Title, pattern, ModerationSearch.LikeEscape));
    }
}
