using GenesisMarket.Api.Auth;
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
/// Рабочая поверхность модератора. Каждая ручка — под своим разрешением
/// (<see cref="Permissions"/>): роль берётся только из claim JWT, разрешение действует
/// лишь при пройденной 2FA. Каждое действие пишется в <c>moderation_logs</c> (таблица
/// только на добавление), включая просмотр контактных данных пользователя. Мутации
/// выполняются в явной транзакции вместе с записью в журнал. Бан — уровень Admin,
/// см. <see cref="AdminUsersController"/>.
/// </summary>
[Route("api/moderation")]
public class ModerationController(
    AppDbContext db,
    IModerationAudit audit,
    IListingDecisions decisions,
    IOptions<ModerationOptions> moderationOptions) : ApiControllerBase
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    /// <summary>
    /// Очередь модерации: объявления, ждущие решения (<c>ReviewQueuedAt</c> заполнен —
    /// и премодерация, и постмодерация), плюс открытые жалобы (New) в едином потоке.
    /// Статус элемента показывает режим: <c>PendingReview</c> — в каталог ещё не пущено,
    /// <c>Active</c> — уже на витрине и проверяется постфактум.
    /// Сортировка: сначала автофлаги и рисковые (по убыванию приоритета), затем по дате
    /// постановки в очередь (старые раньше). Курсорная пагинация. Фильтры: тип
    /// (listing|report), причина (сужает до жалоб), минимальный приоритет.
    /// </summary>
    [Authorize(Policy = Permissions.ListingsModerate)]
    [HttpGet("queue")]
    public async Task<ActionResult<ModerationQueuePage>> GetQueue(
        [FromQuery] ModerationQueueQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        (int Priority, DateTimeOffset CreatedAt, Guid Id)? cursor = null;
        if (query.Cursor is { Length: > 0 } raw)
        {
            if (!ModerationCursor.TryDecode(raw, out var p, out var c, out var i))
                return Problem(title: "Некорректный курсор", statusCode: StatusCodes.Status400BadRequest);
            cursor = (p, c, i);
        }

        // Причина — атрибут жалобы: её фильтр исключает объявления.
        // Приоритет > 0 бывает только у автофлагов-объявлений: исключает жалобы.
        var includeListings = query.Type != ModerationQueueKind.Report && query.Reason is null;
        var includeReports = query.Type != ModerationQueueKind.Listing && query.Priority is null or <= 0;

        var rows = new List<QueueRow>(capacity: (limit + 1) * 2);

        if (includeListings)
            rows.AddRange(await LoadListingRowsAsync(query, cursor, limit, ct));

        if (includeReports)
            rows.AddRange(await LoadReportRowsAsync(query, cursor, limit, ct));

        // Слияние источников: единый порядок (priority DESC, createdAt ASC, id ASC).
        var ordered = rows
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToList();

        var hasMore = ordered.Count > limit;
        var page = ordered.Take(limit).Select(r => r.ToItem()).ToList();
        var next = hasMore
            ? ModerationCursor.Encode(ordered[limit - 1].Priority, ordered[limit - 1].CreatedAt, ordered[limit - 1].Id)
            : null;

        return Ok(new ModerationQueuePage(page, next, hasMore));
    }

    /// <summary>
    /// Полная карточка объявления для модератора: скрытые поля (владелец, приоритет,
    /// статус даже у снятого/удалённого) и открытые жалобы. Query-фильтр мягкого
    /// удаления игнорируется — модератор видит и удалённые объявления.
    /// </summary>
    [Authorize(Policy = Permissions.ListingsModerate)]
    [HttpGet("listings/{id:guid}")]
    public async Task<ActionResult<ModerationListingCard>> GetListing(Guid id, CancellationToken ct)
    {
        var l = await db.Listings.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.Slug, x.Title, x.Description, x.Price, x.PriceType, x.Category,
                x.SubcategoryId, x.City, x.District, x.Condition, x.Status, x.ViewsCount,
                x.FavoritesCount, x.ModerationPriority, x.CreatedAt, x.PublishedAt, x.DeletedAt,
                x.ReviewQueuedAt, x.ApprovedAt,
                x.ReviewAssigneeId, x.ReviewAssignedAt, x.RevisionRequestedAt,
                x.RejectionReasonCode, x.RejectionComment,
                x.OwnerId,
                OwnerCode = x.Owner!.PublicCode,
                OwnerName = x.Owner.Profile!.DisplayName,
                OwnerBanned = x.Owner.IsBanned,
                OwnerApprovedListings = x.Owner.ApprovedListingsCount,
                OwnerLastRejectedAt = x.Owner.LastRejectedAt
            })
            .FirstOrDefaultAsync(ct);

        if (l is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        var reports = await db.Reports.AsNoTracking()
            .Where(r => r.TargetType == ReportTargetType.Listing && r.TargetId == id &&
                        (r.Status == ReportStatus.New || r.Status == ReportStatus.InReview))
            .OrderBy(r => r.CreatedAt)
            .Select(r => new ModerationReportItem(
                r.Id, r.Reason, r.Comment, r.Status, r.ReporterId,
                // Навигации Reporter у Report нет (заявитель может быть анонимом),
                // поэтому код подтягиваем подзапросом.
                db.Users.Where(u => u.Id == r.ReporterId).Select(u => u.PublicCode).FirstOrDefault(),
                r.CreatedAt))
            .ToListAsync(ct);

        var owner = await ModerationReadModels.LoadUserAsync(db, Request, l.OwnerId, ct);
        var assignee = l.ReviewAssigneeId is { } assigneeId
            ? (await ModerationReadModels.LoadActorsAsync(db, [assigneeId], ct)).GetValueOrDefault(assigneeId)
            : null;
        var options = moderationOptions.Value;
        var market = await MarketPrice.EstimateAsync(db, l.SubcategoryId, l.Id, options, ct);

        return Ok(new ModerationListingCard(
            l.Id, l.Slug, l.Title, l.Description, l.Price, l.PriceType, l.Category,
            l.SubcategoryId, l.City, l.District, l.Condition, l.Status, l.ViewsCount,
            l.FavoritesCount, l.ModerationPriority, l.CreatedAt, l.PublishedAt, l.DeletedAt,
            l.OwnerId, l.OwnerCode, l.OwnerName, l.OwnerBanned, reports,
            l.ReviewQueuedAt, l.ApprovedAt, l.OwnerApprovedListings, l.OwnerLastRejectedAt,
            Owner: owner,
            Assignee: assignee,
            ReviewAssignedAt: l.ReviewAssignedAt,
            RevisionRequestedAt: l.RevisionRequestedAt,
            RejectionReasonCode: l.RejectionReasonCode,
            RejectionComment: l.RejectionComment,
            MarketMedianPrice: market.Median,
            MarketSampleSize: market.Sample,
            ReviewSlaMinutes: options.ReviewSlaMinutes));
    }

    /// <summary>
    /// Одобрить объявление: снимает его с очереди модерации и делает активным.
    /// Работает в обоих режимах — и для премодерации (PendingReview → Active,
    /// объявление впервые попадает в каталог), и для постмодерации (объявление уже
    /// на витрине, подтверждаем). Проставляет <c>ApprovedAt</c>: именно с этого момента
    /// объявление идёт в зачёт доверия автора (триггер listings_trust_sync).
    /// </summary>
    [Authorize(Policy = Permissions.ListingsModerate)]
    [HttpPost("listings/{id:guid}/approve")]
    public Task<ActionResult<ModerationActionResult>> Approve(Guid id, CancellationToken ct) =>
        DecideAsync(id, ListingDecision.Approve, reason: null, comment: null, ct);

    /// <summary>
    /// Отклонить объявление: → Rejected. Работает в обоих режимах; при постмодерации
    /// объявление уже было в каталоге, поэтому дополнительно снимается пост в канале.
    /// Автору уходит уведомление с текстом причины через outbox (в той же транзакции).
    /// Отказ фиксируется в <c>users.LastRejectedAt</c> триггером — и на время окна
    /// <c>CatalogHygiene:RejectLookbackDays</c> закрывает автору автопубликацию.
    /// </summary>
    [Authorize(Policy = Permissions.ListingsModerate)]
    [HttpPost("listings/{id:guid}/reject")]
    public Task<ActionResult<ModerationActionResult>> Reject(
        Guid id, RejectListingRequest request, CancellationToken ct) =>
        DecideAsync(id, ListingDecision.Reject, request.Reason, request.Comment, ct);

    /// <summary>
    /// Вернуть автору на доработку: объявление уходит в черновик с причиной. В отличие
    /// от отказа, <c>users.LastRejectedAt</c> не фиксируется и автопубликация автору не
    /// закрывается: исправил — публикует снова по обычным правилам.
    /// </summary>
    [Authorize(Policy = Permissions.ListingsModerate)]
    [HttpPost("listings/{id:guid}/revise")]
    public Task<ActionResult<ModerationActionResult>> Revise(
        Guid id, RejectListingRequest request, CancellationToken ct) =>
        DecideAsync(id, ListingDecision.Revise, request.Reason, request.Comment, ct);

    /// <summary>Одиночное решение: валидация причины, транзакция, решение, журнал — одним коммитом.</summary>
    private async Task<ActionResult<ModerationActionResult>> DecideAsync(
        Guid id, ListingDecision decision, RejectionReasonCode? reason, string? comment, CancellationToken ct)
    {
        if (IListingDecisions.ValidateReason(decision, reason, comment) is { } error)
            return Problem(title: error, statusCode: StatusCodes.Status400BadRequest);

        var listing = await db.Listings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);
        if (listing.OwnerId == CurrentUserId())
            return Problem(title: ConflictOfInterest.OwnListing, statusCode: StatusCodes.Status403Forbidden);
        if (listing.ReviewQueuedAt is null)
            return Problem(title: "Объявление не находится на модерации", statusCode: StatusCodes.Status409Conflict);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var message = await decisions.ApplyAsync(listing, decision, reason, comment, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(new ModerationActionResult(message));
    }

    /// <summary>
    /// Разобрать жалобу: закрыть как Resolved (нарушение подтверждено) или Rejected
    /// (жалоба неверна). Другие статусы недопустимы. Итог фиксируется и в журнале.
    /// </summary>
    [Authorize(Policy = Permissions.ReportsReview)]
    [HttpPost("reports/{id:guid}/resolve")]
    public async Task<ActionResult<ModerationActionResult>> ResolveReport(
        Guid id, ResolveReportRequest request, CancellationToken ct)
    {
        if (request.Status is not (ReportStatus.Resolved or ReportStatus.Rejected))
            return Problem(title: "Жалобу можно закрыть только как Resolved или Rejected",
                statusCode: StatusCodes.Status400BadRequest);

        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (report is null)
            return Problem(title: "Жалоба не найдена", statusCode: StatusCodes.Status404NotFound);
        if (await ConflictOfInterest.ReportInvolvesAsync(db, report, CurrentUserId()!.Value, ct))
            return Problem(title: ConflictOfInterest.OwnReport, statusCode: StatusCodes.Status403Forbidden);
        if (report.Status is ReportStatus.Resolved or ReportStatus.Rejected)
            return Problem(title: "Жалоба уже закрыта", statusCode: StatusCodes.Status409Conflict);

        var now = DateTimeOffset.UtcNow;
        var resolution = string.IsNullOrWhiteSpace(request.Resolution) ? null : request.Resolution.Trim();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        report.Status = request.Status;
        report.ResolvedByUserId = CurrentUserId();
        report.ResolvedAt = now;
        report.Resolution = resolution;
        report.UpdatedAt = now;

        audit.Record(ModerationLog.ActionResolveReport, ModerationLog.TargetReport, id,
            reason: resolution,
            payload: new
            {
                status = request.Status.ToString(),
                resolution,
                targetType = report.TargetType.ToString(),
                targetId = report.TargetId
            },
            waitSince: report.CreatedAt);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(new ModerationActionResult("Жалоба закрыта."));
    }

    /// <summary>
    /// Контактные данные пользователя (email/телефон) — САМАЯ чувствительная ручка.
    /// Каждый вызов пишется в журнал модерации (даже если это просто просмотр).
    /// <para>
    /// Admin и выше (<see cref="Permissions.UsersContacts"/>) видят контакты любого
    /// пользователя. Модератор — только участника жалобы, которую он сам сейчас разбирает:
    /// <paramref name="reportId"/> обязателен, жалоба — в работе и назначена на него.
    /// </para>
    /// </summary>
    // Разрешение жалоб, а не реестра: модератор видит контакты в рамках разбираемой жалобы,
    // полный доступ — отдельное разрешение users.contacts (проверяется внутри).
    [Authorize(Policy = Permissions.ReportsReview)]
    [HttpGet("users/{id:guid}")]
    public Task<ActionResult<ModerationUserContacts>> GetUserContacts(
        Guid id, [FromQuery] Guid? reportId, CancellationToken ct) =>
        UserContactsAsync(u => u.Id == id, reportId, ct);

    /// <summary>
    /// Та же карточка, но по «ID профиля» («82914»), а не по GUID: в жалобах и
    /// в поддержке пользователя называют именно кодом. Ручка закрыта служебным разрешением
    /// намеренно — кодов всего 90 000, и открытый резолвер «код → аккаунт»
    /// превратил бы их в перечислимое пространство имён. Просмотр пишется в журнал
    /// так же, как и обращение по GUID.
    /// </summary>
    [Authorize(Policy = Permissions.ReportsReview)]
    [HttpGet("users/by-code/{code}")]
    public Task<ActionResult<ModerationUserContacts>> GetUserContactsByCode(
        string code, [FromQuery] Guid? reportId, CancellationToken ct) =>
        UserContactsAsync(u => u.PublicCode == code, reportId, ct);

    private const string ContactsOutsideReport =
        "Контакты доступны только участникам жалобы, которую вы сейчас разбираете";

    private async Task<ActionResult<ModerationUserContacts>> UserContactsAsync(
        System.Linq.Expressions.Expression<Func<User, bool>> match, Guid? reportId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(match)
            .Where(u => !u.IsDeleted)
            .Select(u => new ModerationUserContacts(
                u.Id, u.PublicCode, u.Email, u.PhoneE164, u.EmailVerified, u.PhoneVerified,
                u.ProjectRoles.Where(r => r.ProjectKey == Projects.Market).Select(r => (UserRole?)r.Role).FirstOrDefault() ?? UserRole.User,
                u.IsBanned, u.BannedUntil, u.CreatedAt))
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        // Без полного доступа — только в рамках своей жалобы в работе и только её участники.
        // Ответ не различает «нет жалобы» и «не участник»: иначе ручка подсказывала бы связи.
        if (!CurrentUser.Can(Permissions.UsersContacts))
        {
            var me = CurrentUserId()!.Value;
            var report = reportId is { } rid
                ? await db.Reports.AsNoTracking().FirstOrDefaultAsync(r =>
                    r.Id == rid && r.Status == ReportStatus.InReview && r.AssignedToUserId == me, ct)
                : null;
            if (report is null || !await ConflictOfInterest.IsReportParticipantAsync(db, report, user.Id, ct))
                return Problem(title: ContactsOutsideReport, statusCode: StatusCodes.Status403Forbidden);
        }

        // Обязательно фиксируем факт просмотра чувствительных данных (и в рамках какой жалобы).
        audit.Record(ModerationLog.ActionViewUserContacts, ModerationLog.TargetUser, user.Id,
            payload: reportId is null ? null : new { reportId });
        await db.SaveChangesAsync(ct);

        return Ok(user);
    }

    /// <summary>
    /// Счётчики очереди и активности модерации за сегодня/неделю — из них бейджи меню и вкладки,
    /// нужны каждому, кто разбирает очередь. Сам журнал (кто что решил) — только Admin.
    /// </summary>
    [Authorize(Policy = Permissions.ListingsModerate)]
    [HttpGet("stats")]
    public async Task<ActionResult<ModerationStats>> GetStats(CancellationToken ct)
    {
        var todayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var weekStart = todayStart.AddDays(-7);

        // Премодерация и постмодерация считаются раздельно: первая — очередь, которая
        // держит объявления вне каталога (её просрочка видна пользователям), вторая —
        // выборочная проверка уже опубликованного (её просрочка видна только рискам).
        var pendingListings = await db.Listings.CountAsync(
            l => l.ReviewQueuedAt != null && l.Status == ListingStatus.PendingReview, ct);
        var postReviewListings = await db.Listings.CountAsync(
            l => l.ReviewQueuedAt != null && l.Status == ListingStatus.Active, ct);
        var openReports = await db.Reports.CountAsync(r => r.Status == ReportStatus.New, ct);
        var actionsToday = await db.ModerationLogs.CountAsync(m => m.CreatedAt >= todayStart, ct);
        var actionsThisWeek = await db.ModerationLogs.CountAsync(m => m.CreatedAt >= weekStart, ct);
        var bansToday = await db.ModerationLogs.CountAsync(
            m => m.Action == ModerationLog.ActionBanUser && m.CreatedAt >= todayStart, ct);

        // ---- дашборд рабочего места ----
        var now = DateTimeOffset.UtcNow;
        var sla = moderationOptions.Value.ReviewSlaMinutes;
        var overdueSince = now.AddMinutes(-sla);
        var yesterdayStart = todayStart.AddDays(-1);

        var revisionListings = await db.Listings.CountAsync(
            l => l.RevisionRequestedAt != null && l.Status == ListingStatus.Draft, ct);
        var inReviewReports = await db.Reports.CountAsync(r => r.Status == ReportStatus.InReview, ct);
        var oldestQueuedAt = await db.Listings
            .Where(l => l.ReviewQueuedAt != null)
            .MinAsync(l => l.ReviewQueuedAt, ct);
        var overdue = await db.Listings.CountAsync(
            l => l.ReviewQueuedAt != null && l.ReviewQueuedAt < overdueSince, ct);
        var actionsYesterday = await db.ModerationLogs.CountAsync(
            m => m.CreatedAt >= yesterdayStart && m.CreatedAt < todayStart, ct);
        var activeBans = await db.Users.CountAsync(
            u => !u.IsDeleted && u.IsBanned && (u.BannedUntil == null || u.BannedUntil > now), ct);
        var pendingBusiness = await db.BusinessProfiles.CountAsync(
            b => b.Status == BusinessVerificationStatus.Pending, ct);

        string[] listingDecisions =
        [
            ModerationLog.ActionApproveListing, ModerationLog.ActionRejectListing,
            ModerationLog.ActionReviseListing
        ];

        // Решения за сутки — в память: их немного, а из них собираются и счётчики за
        // сегодня, и SLA, и почасовая лента (разбивать это на пять запросов незачем).
        var since = todayStart < now.AddHours(-12) ? todayStart : now.AddHours(-12);
        var recent = await db.ModerationLogs.AsNoTracking()
            .Where(m => m.CreatedAt >= since && listingDecisions.Contains(m.Action))
            .Select(m => new { m.Action, m.CreatedAt, m.WaitSeconds })
            .ToListAsync(ct);

        var today = recent.Where(m => m.CreatedAt >= todayStart).ToList();
        var measured = today.Where(m => m.WaitSeconds != null).ToList();
        double? avgWait = measured.Count == 0 ? null : Math.Round(measured.Average(m => m.WaitSeconds!.Value), 1);
        double? slaPercent = measured.Count == 0
            ? null
            : Math.Round(100.0 * measured.Count(m => m.WaitSeconds <= sla * 60) / measured.Count, 1);

        var hourly = new int[12];
        foreach (var m in recent)
        {
            var hoursAgo = (int)Math.Floor((now - m.CreatedAt).TotalHours);
            if (hoursAgo is >= 0 and < 12)
                hourly[11 - hoursAgo]++;
        }

        return Ok(new ModerationStats(
            pendingListings, openReports, pendingListings + postReviewListings + openReports,
            actionsToday, actionsThisWeek, bansToday, postReviewListings,
            RevisionListings: revisionListings,
            InReviewReports: inReviewReports,
            ApprovalsToday: today.Count(m => m.Action == ModerationLog.ActionApproveListing),
            RejectionsToday: today.Count(m => m.Action == ModerationLog.ActionRejectListing),
            RevisionsToday: today.Count(m => m.Action == ModerationLog.ActionReviseListing),
            ActionsYesterday: actionsYesterday,
            ActiveBans: activeBans,
            PendingBusinessApplications: pendingBusiness,
            OldestQueuedAt: oldestQueuedAt,
            OverdueListings: overdue,
            ReviewSlaMinutes: sla,
            AvgWaitSecondsToday: avgWait,
            SlaPercentToday: slaPercent,
            DecisionsLast12h: hourly));
    }

    // ---- queue helpers ----

    private async Task<IEnumerable<QueueRow>> LoadListingRowsAsync(
        ModerationQueueQuery query, (int Priority, DateTimeOffset CreatedAt, Guid Id)? cursor, int limit, CancellationToken ct)
    {
        // Очередь = «стоит на проверке», а не «скрыто из каталога»: постмодерация
        // держит объявление активным, но в очереди (см. Listing.ReviewQueuedAt).
        var q = db.Listings.AsNoTracking().Where(l => l.ReviewQueuedAt != null);

        if (query.Priority is { } minPriority && minPriority > 0)
            q = q.Where(l => l.ModerationPriority >= minPriority);

        if (cursor is { } c)
            q = q.Where(l =>
                l.ModerationPriority < c.Priority ||
                (l.ModerationPriority == c.Priority && l.ReviewQueuedAt > c.CreatedAt) ||
                (l.ModerationPriority == c.Priority && l.ReviewQueuedAt == c.CreatedAt && l.Id > c.Id));

        var rows = await q
            .OrderByDescending(l => l.ModerationPriority)
            .ThenBy(l => l.ReviewQueuedAt)
            .ThenBy(l => l.Id)
            .Take(limit + 1)
            .Select(l => new { l.Id, l.ModerationPriority, QueuedAt = l.ReviewQueuedAt!.Value, l.Status, l.Title })
            .ToListAsync(ct);

        // В элемент очереди отдаём время ПОСТАНОВКИ в очередь, а не создания объявления:
        // по нему строится FIFO, и по нему же продолжает страницу курсор.
        return rows.Select(l => new QueueRow(
            ModerationQueueKind.Listing, l.Id, l.ModerationPriority, l.QueuedAt, l.Status.ToString(),
            Title: l.Title));
    }

    private async Task<IEnumerable<QueueRow>> LoadReportRowsAsync(
        ModerationQueueQuery query, (int Priority, DateTimeOffset CreatedAt, Guid Id)? cursor, int limit, CancellationToken ct)
    {
        var q = db.Reports.AsNoTracking().Where(r => r.Status == ReportStatus.New);

        if (query.Reason is { } reason)
            q = q.Where(r => r.Reason == reason);

        // Приоритет жалоб фиксирован (0). Keyset относительно курсора:
        if (cursor is { } c)
        {
            if (c.Priority == 0)
                q = q.Where(r => r.CreatedAt > c.CreatedAt || (r.CreatedAt == c.CreatedAt && r.Id > c.Id));
            else if (c.Priority < 0)
                q = q.Where(_ => false);
            // c.Priority > 0: все жалобы идут после (0 < priority) — фильтр не нужен.
        }

        var rows = await q
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Take(limit + 1)
            .Select(r => new { r.Id, r.CreatedAt, r.Status, r.Reason, r.TargetType, r.TargetId })
            .ToListAsync(ct);

        return rows.Select(r => new QueueRow(
            ModerationQueueKind.Report, r.Id, 0, r.CreatedAt, r.Status.ToString(),
            ReportTargetType: r.TargetType, ReportTargetId: r.TargetId, Reason: r.Reason));
    }

    /// <summary>Единая строка очереди для слияния объявлений и жалоб.</summary>
    private sealed record QueueRow(
        ModerationQueueKind Kind,
        Guid Id,
        int Priority,
        DateTimeOffset CreatedAt,
        string Status,
        string? Title = null,
        ReportTargetType? ReportTargetType = null,
        Guid? ReportTargetId = null,
        ReportReason? Reason = null)
    {
        public ModerationQueueItem ToItem() => new(
            Kind, Id, Priority, CreatedAt, Status, Title, ReportTargetType, ReportTargetId, Reason);
    }
}
