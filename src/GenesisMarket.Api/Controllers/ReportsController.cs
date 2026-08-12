using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Security;
using GenesisMarket.Api.Trust;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Controllers;

/// <summary>
/// Приём жалоб на объявления, пользователей и отзывы. Открыт анонимам —
/// мошенничество часто замечает тот, кто ещё не зарегистрирован. Rate-limit,
/// дедупликация повторов от одного репортёра и автоматика: N независимых жалоб
/// Fraud/Prohibited на объявление переводят его в PendingReview и в начало
/// очереди модерации.
/// </summary>
[Route("api/reports")]
public class ReportsController(
    AppDbContext db,
    IIpHasher ipHasher,
    IOptions<TrustOptions> options,
    ILogger<ReportsController> logger) : ApiControllerBase
{
    private readonly TrustOptions _o = options.Value;

    /// <summary>
    /// Подать жалобу. Дубликат (тот же репортёр + тот же объект среди открытых
    /// жалоб) — 200 без новой записи. Иначе 201. Несуществующий объект — 404.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Report)]
    [HttpPost]
    public async Task<ActionResult<ReportResponse>> Create(CreateReportRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        var ipHash = ipHasher.Hash(ClientIp());

        // Rate-limit приёма жалоб — на встроенном RateLimiter (политика "report"):
        // аноним по IP (5/час), авторизованный по пользователю (20/час).

        if (!await TargetExistsAsync(request.TargetType, request.TargetId, ct))
            return Problem(title: "Объект жалобы не найден", statusCode: StatusCodes.Status404NotFound);

        // Дедупликация повторной жалобы от того же репортёра на тот же объект.
        var duplicate = await FindActiveDuplicateAsync(request, userId, ipHash, ct);
        if (duplicate is not null)
            return Ok(Map(duplicate));

        var report = new Report
        {
            TargetType = request.TargetType,
            TargetId = request.TargetId,
            ReporterId = userId,
            // IpHash храним только у анонима — для дедупа и подсчёта независимости.
            ReporterIpHash = userId is null ? ipHash : null,
            Reason = request.Reason,
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim()
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);

        // Автоматика — в той же транзакции, что и запись жалобы.
        if (request.TargetType == ReportTargetType.Listing &&
            request.Reason is ReportReason.Fraud or ReportReason.Prohibited)
            await MaybeAutoFlagListingAsync(request.TargetId, ct);

        await tx.CommitAsync(ct);

        return Created((string?)null, Map(report));
    }

    private async Task<bool> TargetExistsAsync(ReportTargetType type, Guid id, CancellationToken ct) => type switch
    {
        // Объявление могло быть скрыто/архивировано — жаловаться всё равно можно.
        ReportTargetType.Listing => await db.Listings.IgnoreQueryFilters().AnyAsync(l => l.Id == id, ct),
        ReportTargetType.User => await db.Users.AnyAsync(u => u.Id == id && !u.IsDeleted, ct),
        ReportTargetType.Review => await db.Reviews.AnyAsync(r => r.Id == id, ct),
        _ => false
    };

    private async Task<Report?> FindActiveDuplicateAsync(
        CreateReportRequest request, Guid? userId, string? ipHash, CancellationToken ct)
    {
        var query = db.Reports.AsNoTracking().Where(r =>
            r.TargetType == request.TargetType &&
            r.TargetId == request.TargetId &&
            (r.Status == ReportStatus.New || r.Status == ReportStatus.InReview));

        if (userId is { } uid)
            query = query.Where(r => r.ReporterId == uid);
        else if (ipHash is not null)
            query = query.Where(r => r.ReporterId == null && r.ReporterIpHash == ipHash);
        else
            return null; // Аноним без ключа хеширования IP — дедуп невозможен.

        return await query.FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Если по объявлению набралось ≥ порога НЕЗАВИСИМЫХ открытых жалоб
    /// Fraud/Prohibited — переводим Active → PendingReview и поднимаем приоритет
    /// очереди. Независимость: разные ReporterId, а для анонимов — разные IpHash.
    /// </summary>
    private async Task MaybeAutoFlagListingAsync(Guid listingId, CancellationToken ct)
    {
        var reporters = await db.Reports.AsNoTracking()
            .Where(r =>
                r.TargetType == ReportTargetType.Listing &&
                r.TargetId == listingId &&
                (r.Status == ReportStatus.New || r.Status == ReportStatus.InReview) &&
                (r.Reason == ReportReason.Fraud || r.Reason == ReportReason.Prohibited))
            .Select(r => new { r.ReporterId, r.ReporterIpHash })
            .ToListAsync(ct);

        var independent = reporters
            .Select(r => r.ReporterId?.ToString() ?? r.ReporterIpHash)
            .Where(key => key is not null)
            .Distinct()
            .Count();

        if (independent < _o.AutoFlagThreshold)
            return;

        // Трогаем только активные объявления; проданные/снятые не воскрешаем.
        var affected = await db.Listings
            .Where(l => l.Id == listingId && l.Status == ListingStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ListingStatus.PendingReview)
                .SetProperty(l => l.ModerationPriority, _o.AutoFlagPriority), ct);

        if (affected > 0)
            logger.LogWarning(
                "Объявление {ListingId} автоматически отправлено на модерацию: {Count} независимых жалоб Fraud/Prohibited (порог {Threshold})",
                listingId, independent, _o.AutoFlagThreshold);
    }

    private static ReportResponse Map(Report r) => new(
        r.Id, r.TargetType, r.TargetId, r.Reason, r.Comment, r.Status, r.CreatedAt);
}
