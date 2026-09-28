using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Moderation;

/// <summary>
/// Конфликт интересов сотрудника: решения по своему объявлению, жалобе на себя или
/// своё, отзыву о себе или своему. Проверяется на сервере в каждом эндпоинте-решении
/// (одиночном и пакетном) — роль Owner от правила не освобождает.
/// </summary>
public static class ConflictOfInterest
{
    public const string OwnListing = "Нельзя модерировать своё объявление";
    public const string OwnReport = "Нельзя разбирать жалобу, которую вы подали или которая касается вас или ваших объявлений";
    public const string OwnReview = "Нельзя модерировать отзыв о себе или свой отзыв";

    /// <summary>
    /// Сотрудник — участник жалобы: он её подал, либо она направлена на него, его объявление
    /// или отзыв, где он автор или адресат. Участнику разбирать жалобу нельзя.
    /// </summary>
    public static Task<bool> ReportInvolvesAsync(AppDbContext db, Report report, Guid actorId, CancellationToken ct) =>
        IsReportParticipantAsync(db, report, actorId, ct);

    /// <summary>
    /// Пользователь — участник жалобы: автор жалобы или её сторона (цель, владелец
    /// объявления, автор или адресат отзыва). Одно определение на два правила: конфликт
    /// интересов сотрудника и чьи контакты модератор видит в рамках разбираемой жалобы.
    /// </summary>
    public static async Task<bool> IsReportParticipantAsync(AppDbContext db, Report report, Guid userId, CancellationToken ct)
    {
        if (report.ReporterId == userId)
            return true;

        return report.TargetType switch
        {
            ReportTargetType.User => report.TargetId == userId,
            ReportTargetType.Listing => await db.Listings.IgnoreQueryFilters()
                .AnyAsync(l => l.Id == report.TargetId && l.OwnerId == userId, ct),
            ReportTargetType.Review => await db.Reviews
                .AnyAsync(r => r.Id == report.TargetId && (r.AuthorId == userId || r.TargetUserId == userId), ct),
            _ => false
        };
    }

    /// <summary>То же по идентификатору жалобы (для условных UPDATE, где сущность не загружается).</summary>
    public static async Task<bool> ReportInvolvesAsync(AppDbContext db, Guid reportId, Guid actorId, CancellationToken ct)
    {
        var report = await db.Reports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reportId, ct);
        return report is not null && await ReportInvolvesAsync(db, report, actorId, ct);
    }

    public static bool ReviewInvolves(Review review, Guid actorId) =>
        review.AuthorId == actorId || review.TargetUserId == actorId;
}
