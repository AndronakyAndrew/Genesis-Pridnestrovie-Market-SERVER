using System.Text.Json;
using GenesisMarket.Api.Auth;
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
/// Рабочая поверхность модератора. Все ручки под policy «Moderator» (роль берётся
/// только из claim JWT — никаких хардкод-списков email/админов). Каждое действие
/// пишется в <c>moderation_logs</c> (таблица только на добавление), включая просмотр
/// контактных данных пользователя. Мутации выполняются в явной транзакции вместе
/// с записью в журнал.
/// </summary>
[Route("api/moderation")]
[Authorize(Policy = "Moderator")]
public class ModerationController(
    AppDbContext db,
    IModerationAudit audit,
    IRefreshTokenService refreshTokens,
    SecurityStampValidator securityStamp,
    ILogger<ModerationController> logger) : ApiControllerBase
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    // Статусы объявлений, снимаемые с публикации при бане владельца.
    private static readonly ListingStatus[] LiveStatuses =
        [ListingStatus.Active, ListingStatus.PendingReview];

    /// <summary>
    /// Очередь модерации: объявления на премодерации (PendingReview) и открытые
    /// жалобы (New) в едином потоке. Сортировка: сначала автофлаги (по убыванию
    /// приоритета), затем по дате (старые раньше). Курсорная пагинация. Фильтры:
    /// тип (listing|report), причина (сужает до жалоб), минимальный приоритет.
    /// </summary>
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
                x.OwnerId,
                OwnerName = x.Owner!.Profile!.DisplayName,
                OwnerBanned = x.Owner.IsBanned
            })
            .FirstOrDefaultAsync(ct);

        if (l is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);

        var reports = await db.Reports.AsNoTracking()
            .Where(r => r.TargetType == ReportTargetType.Listing && r.TargetId == id &&
                        (r.Status == ReportStatus.New || r.Status == ReportStatus.InReview))
            .OrderBy(r => r.CreatedAt)
            .Select(r => new ModerationReportItem(r.Id, r.Reason, r.Comment, r.Status, r.ReporterId, r.CreatedAt))
            .ToListAsync(ct);

        return Ok(new ModerationListingCard(
            l.Id, l.Slug, l.Title, l.Description, l.Price, l.PriceType, l.Category,
            l.SubcategoryId, l.City, l.District, l.Condition, l.Status, l.ViewsCount,
            l.FavoritesCount, l.ModerationPriority, l.CreatedAt, l.PublishedAt, l.DeletedAt,
            l.OwnerId, l.OwnerName, l.OwnerBanned, reports));
    }

    /// <summary>Одобрить объявление: PendingReview → Active, приоритет очереди сбрасывается.</summary>
    [HttpPost("listings/{id:guid}/approve")]
    public async Task<ActionResult<ModerationActionResult>> Approve(Guid id, CancellationToken ct)
    {
        var listing = await db.Listings.IgnoreQueryFilters()
            .Where(l => l.Id == id)
            .Select(l => new { l.Status, l.PublishedAt })
            .FirstOrDefaultAsync(ct);

        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);
        if (listing.Status != ListingStatus.PendingReview)
            return Problem(title: "Объявление не находится на модерации", statusCode: StatusCodes.Status409Conflict);

        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.Listings.IgnoreQueryFilters()
            .Where(l => l.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ListingStatus.Active)
                .SetProperty(l => l.ModerationPriority, 0)
                .SetProperty(l => l.PublishedAt, l => l.PublishedAt ?? now)
                .SetProperty(l => l.UpdatedAt, now), ct);

        // Уведомление автора об одобрении — через outbox в той же транзакции.
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.ListingApproved,
            Payload = JsonSerializer.Serialize(new { listingId = id })
        });

        audit.Record(ModerationLog.ActionApproveListing, ModerationLog.TargetListing, id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(new ModerationActionResult("Объявление одобрено и опубликовано."));
    }

    /// <summary>
    /// Отклонить объявление: → Rejected. Автору уходит уведомление с текстом причины
    /// через outbox (в той же транзакции). Причина и комментарий пишутся в журнал.
    /// </summary>
    [HttpPost("listings/{id:guid}/reject")]
    public async Task<ActionResult<ModerationActionResult>> Reject(
        Guid id, RejectListingRequest request, CancellationToken ct)
    {
        var listing = await db.Listings.IgnoreQueryFilters()
            .Where(l => l.Id == id)
            .Select(l => new { l.Status, l.OwnerId, l.Title })
            .FirstOrDefaultAsync(ct);

        if (listing is null)
            return Problem(title: "Объявление не найдено", statusCode: StatusCodes.Status404NotFound);
        if (listing.Status != ListingStatus.PendingReview)
            return Problem(title: "Объявление не находится на модерации", statusCode: StatusCodes.Status409Conflict);

        var now = DateTimeOffset.UtcNow;
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.Listings.IgnoreQueryFilters()
            .Where(l => l.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ListingStatus.Rejected)
                .SetProperty(l => l.ModerationPriority, 0)
                .SetProperty(l => l.UpdatedAt, now), ct);

        // Уведомление автора — через outbox (в той же транзакции). Только идентификаторы
        // и причина: текст письма/сообщения соберёт обработчик по типу.
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.ListingRejected,
            Payload = JsonSerializer.Serialize(new
            {
                listingId = id,
                reason = request.Reason.ToString(),
                comment
            })
        });

        audit.Record(ModerationLog.ActionRejectListing, ModerationLog.TargetListing, id,
            reason: comment,
            payload: new { reason = request.Reason.ToString(), comment });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(new ModerationActionResult("Объявление отклонено, автор уведомлён."));
    }

    /// <summary>
    /// Разобрать жалобу: закрыть как Resolved (нарушение подтверждено) или Rejected
    /// (жалоба неверна). Другие статусы недопустимы. Итог фиксируется и в журнале.
    /// </summary>
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
            });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(new ModerationActionResult("Жалоба закрыта."));
    }

    /// <summary>
    /// Забанить пользователя. В ОДНОЙ транзакции: IsBanned/BannedUntil, ротация
    /// SecurityStamp (иначе выданный access-токен жил бы ещё до 15 минут), архивация
    /// активных объявлений, отзыв всех refresh-токенов, запись в журнал. После
    /// коммита сбрасывается кэш снимка пользователя — бан действует немедленно.
    /// </summary>
    [HttpPost("users/{id:guid}/ban")]
    public async Task<ActionResult<ModerationActionResult>> Ban(
        Guid id, BanUserRequest request, CancellationToken ct)
    {
        if (id == CurrentUserId())
            return Problem(title: "Нельзя забанить самого себя", statusCode: StatusCodes.Status400BadRequest);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);
        if (user.Role == UserRole.Admin)
            return Problem(title: "Нельзя забанить администратора", statusCode: StatusCodes.Status403Forbidden);

        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        user.IsBanned = true;
        user.BannedUntil = request.Until;
        user.SecurityStamp = Guid.NewGuid();   // немедленно инвалидирует выданные access-токены
        user.UpdatedAt = now;

        // Активные объявления забаненного скрываем из каталога.
        var archived = await db.Listings
            .Where(l => l.OwnerId == id && LiveStatuses.Contains(l.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ListingStatus.Archived)
                .SetProperty(l => l.UpdatedAt, now), ct);

        // Все refresh-токены отзываются (в этой же транзакции — общий DbContext).
        await refreshTokens.RevokeAllAsync(id, ct);

        audit.Record(ModerationLog.ActionBanUser, ModerationLog.TargetUser, id,
            reason: request.Reason,
            payload: new { until = request.Until, reason = request.Reason, archivedListings = archived });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Снимок пользователя кэшируется в SecurityStampValidator — сбрасываем, чтобы бан подействовал сразу.
        securityStamp.Invalidate(id);

        logger.LogInformation("Пользователь {UserId} забанен модератором {ActorId}. Архивировано объявлений: {Count}.",
            id, CurrentUserId(), archived);

        return Ok(new ModerationActionResult("Пользователь заблокирован."));
    }

    /// <summary>Снять бан: IsBanned = false, BannedUntil = null. Объявления не восстанавливаются автоматически.</summary>
    [HttpPost("users/{id:guid}/unban")]
    public async Task<ActionResult<ModerationActionResult>> Unban(Guid id, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);
        if (!user.IsBanned)
            return Problem(title: "Пользователь не заблокирован", statusCode: StatusCodes.Status409Conflict);

        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        user.IsBanned = false;
        user.BannedUntil = null;
        user.UpdatedAt = now;

        audit.Record(ModerationLog.ActionUnbanUser, ModerationLog.TargetUser, id);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Сбрасываем кэш снимка: иначе SecurityStampValidator до TTL считал бы пользователя забаненным.
        securityStamp.Invalidate(id);

        return Ok(new ModerationActionResult("Пользователь разблокирован."));
    }

    /// <summary>
    /// Контактные данные пользователя (email/телефон) — САМАЯ чувствительная ручка.
    /// Каждый вызов пишется в журнал модерации (даже если это просто просмотр).
    /// </summary>
    [HttpGet("users/{id:guid}")]
    public async Task<ActionResult<ModerationUserContacts>> GetUserContacts(Guid id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == id && !u.IsDeleted)
            .Select(u => new ModerationUserContacts(
                u.Id, u.Email, u.PhoneE164, u.EmailVerified, u.PhoneVerified,
                u.Role, u.IsBanned, u.BannedUntil, u.CreatedAt))
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return Problem(title: "Пользователь не найден", statusCode: StatusCodes.Status404NotFound);

        // Обязательно фиксируем факт просмотра чувствительных данных.
        audit.Record(ModerationLog.ActionViewUserContacts, ModerationLog.TargetUser, id);
        await db.SaveChangesAsync(ct);

        return Ok(user);
    }

    /// <summary>Счётчики очереди и активности модерации за сегодня/неделю.</summary>
    [HttpGet("stats")]
    public async Task<ActionResult<ModerationStats>> GetStats(CancellationToken ct)
    {
        var todayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var weekStart = todayStart.AddDays(-7);

        var pendingListings = await db.Listings.CountAsync(l => l.Status == ListingStatus.PendingReview, ct);
        var openReports = await db.Reports.CountAsync(r => r.Status == ReportStatus.New, ct);
        var actionsToday = await db.ModerationLogs.CountAsync(m => m.CreatedAt >= todayStart, ct);
        var actionsThisWeek = await db.ModerationLogs.CountAsync(m => m.CreatedAt >= weekStart, ct);
        var bansToday = await db.ModerationLogs.CountAsync(
            m => m.Action == ModerationLog.ActionBanUser && m.CreatedAt >= todayStart, ct);

        return Ok(new ModerationStats(
            pendingListings, openReports, pendingListings + openReports,
            actionsToday, actionsThisWeek, bansToday));
    }

    // ---- queue helpers ----

    private async Task<IEnumerable<QueueRow>> LoadListingRowsAsync(
        ModerationQueueQuery query, (int Priority, DateTimeOffset CreatedAt, Guid Id)? cursor, int limit, CancellationToken ct)
    {
        var q = db.Listings.AsNoTracking().Where(l => l.Status == ListingStatus.PendingReview);

        if (query.Priority is { } minPriority && minPriority > 0)
            q = q.Where(l => l.ModerationPriority >= minPriority);

        if (cursor is { } c)
            q = q.Where(l =>
                l.ModerationPriority < c.Priority ||
                (l.ModerationPriority == c.Priority && l.CreatedAt > c.CreatedAt) ||
                (l.ModerationPriority == c.Priority && l.CreatedAt == c.CreatedAt && l.Id > c.Id));

        var rows = await q
            .OrderByDescending(l => l.ModerationPriority)
            .ThenBy(l => l.CreatedAt)
            .ThenBy(l => l.Id)
            .Take(limit + 1)
            .Select(l => new { l.Id, l.ModerationPriority, l.CreatedAt, l.Status, l.Title })
            .ToListAsync(ct);

        return rows.Select(l => new QueueRow(
            ModerationQueueKind.Listing, l.Id, l.ModerationPriority, l.CreatedAt, l.Status.ToString(),
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
