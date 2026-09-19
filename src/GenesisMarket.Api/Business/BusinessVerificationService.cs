using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Moderation;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Business;

/// <summary>
/// Решения модератора по заявкам бизнеса — ЕДИНСТВЕННОЕ место, где статус
/// становится Verified или Rejected. Вызывается только из контроллера под
/// policy «Moderator».
///
/// Решение — условный UPDATE (compare-and-set): строка меняется, только если она
/// всё ещё Pending, режим включён и момент подачи совпадает с тем, что видел
/// модератор. Так решение не ляжет ни на отозванную заявку, ни на заявку,
/// поданную заново с другими реквизитами между просмотром и кликом. Запись в
/// журнал модерации — в той же транзакции.
/// </summary>
public interface IBusinessVerificationService
{
    Task<(BusinessApplicationsPage? Page, BusinessError? Error)> ListAsync(
        BusinessApplicationsQuery query, CancellationToken ct);

    Task<BusinessError?> ApproveAsync(Guid userId, DateTimeOffset? submittedAt, CancellationToken ct);

    Task<BusinessError?> RejectAsync(Guid userId, DateTimeOffset? submittedAt, string? reason, CancellationToken ct);
}

public sealed class BusinessVerificationService(
    AppDbContext db,
    IModerationAudit audit,
    ICurrentUser currentUser,
    TimeProvider clock) : IBusinessVerificationService
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    public async Task<(BusinessApplicationsPage? Page, BusinessError? Error)> ListAsync(
        BusinessApplicationsQuery query, CancellationToken ct)
    {
        var status = query.Status ?? BusinessVerificationStatus.Pending;
        if (status == BusinessVerificationStatus.None)
            return (null, new BusinessError(StatusCodes.Status400BadRequest,
                BusinessErrorCodes.InvalidStatusFilter,
                "Фильтр по статусу: Pending, Verified или Rejected", Field: "status"));

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        var q = db.BusinessProfiles.AsNoTracking()
            .Where(b => b.Status == status && b.SubmittedAt != null && !b.User!.IsDeleted);

        // Курсор очереди модерации переиспользуется: приоритет у заявок всегда 0,
        // порядок — (SubmittedAt, UserId) по возрастанию, то есть FIFO подачи.
        if (query.Cursor is { Length: > 0 } raw)
        {
            if (!ModerationCursor.TryDecode(raw, out _, out var afterAt, out var afterId))
                return (null, new BusinessError(StatusCodes.Status400BadRequest,
                    BusinessErrorCodes.InvalidCursor, "Некорректный курсор", Field: "cursor"));

            q = q.Where(b => b.SubmittedAt > afterAt || (b.SubmittedAt == afterAt && b.UserId > afterId));
        }

        var rows = await q
            .OrderBy(b => b.SubmittedAt)
            .ThenBy(b => b.UserId)
            .Take(limit + 1)
            .Select(b => new BusinessApplicationItem(
                b.UserId,
                b.User!.PublicCode,
                b.User.Profile!.DisplayName,
                new BusinessDetailsDto(b.ShopName, b.LegalForm, b.RegistrationNumber, b.PickupAddress),
                b.AccountType,
                b.Status,
                b.SubmittedAt!.Value,
                b.ReviewedAt,
                b.ReviewedByUserId,
                b.RejectionReason,
                b.RejectionCount,
                b.User.EmailVerified,
                b.User.PhoneVerified))
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        var next = hasMore
            ? ModerationCursor.Encode(0, page[^1].SubmittedAt, page[^1].UserId)
            : null;

        return (new BusinessApplicationsPage(page, next, hasMore), null);
    }

    public async Task<BusinessError?> ApproveAsync(Guid userId, DateTimeOffset? submittedAt, CancellationToken ct)
    {
        if (Precheck(userId, submittedAt) is { } invalid)
            return invalid;

        // Npgsql пишет в timestamptz только offset 0 — клиент мог прислать момент в своём поясе.
        var version = submittedAt!.Value.ToUniversalTime();
        var actorId = currentUser.UserId!.Value;
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var app = await db.BusinessProfiles.AsNoTracking()
            .Where(b => b.UserId == userId)
            .Select(b => new { b.Status, b.SubmittedAt, b.RegistrationNumber, b.LegalForm, b.ShopName })
            .FirstOrDefaultAsync(ct);

        if (app is null)
            return NotFound();
        if (app.Status != BusinessVerificationStatus.Pending || app.SubmittedAt != version)
            return Changed();

        var taken = await db.BusinessProfiles.AsNoTracking().AnyAsync(b =>
            b.UserId != userId &&
            b.RegistrationNumber == app.RegistrationNumber &&
            b.Status == BusinessVerificationStatus.Verified, ct);
        if (taken)
            return RegistrationNumberTaken();

        int updated;
        try
        {
            updated = await PendingApplication(userId, version)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BusinessVerificationStatus.Verified)
                    .SetProperty(b => b.RejectionReason, (string?)null)
                    .SetProperty(b => b.ReviewedAt, now)
                    .SetProperty(b => b.ReviewedByUserId, actorId)
                    .SetProperty(b => b.UpdatedAt, now), ct);
        }
        // Гонка двух одобрений одного номера: проверку выше прошли оба, индекс пропустил одного.
        catch (Exception ex) when (ex.IsUniqueViolation())
        {
            return RegistrationNumberTaken();
        }

        if (updated == 0)
            return Changed();

        audit.Record(ModerationLog.ActionApproveBusiness, ModerationLog.TargetBusiness, userId,
            payload: new { submittedAt = version, legalForm = app.LegalForm.ToString(), shopName = app.ShopName });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    public async Task<BusinessError?> RejectAsync(
        Guid userId, DateTimeOffset? submittedAt, string? reason, CancellationToken ct)
    {
        if (Precheck(userId, submittedAt) is { } invalid)
            return invalid;

        var text = BusinessInput.NormalizeText(reason);
        if (text.Length == 0)
            return new BusinessError(StatusCodes.Status400BadRequest,
                BusinessErrorCodes.RejectionReasonRequired,
                "Укажите причину отклонения — по ней пользователь исправит данные", Field: "reason");
        if (text.Length > BusinessInput.RejectionReasonMax)
            return new BusinessError(StatusCodes.Status400BadRequest,
                BusinessErrorCodes.RejectionReasonLength,
                $"Причина — не длиннее {BusinessInput.RejectionReasonMax} символов", Field: "reason");

        var version = submittedAt!.Value.ToUniversalTime();
        var actorId = currentUser.UserId!.Value;
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var app = await db.BusinessProfiles.AsNoTracking()
            .Where(b => b.UserId == userId)
            .Select(b => new { b.Status, b.SubmittedAt, b.RejectionCount })
            .FirstOrDefaultAsync(ct);

        if (app is null)
            return NotFound();
        if (app.Status != BusinessVerificationStatus.Pending || app.SubmittedAt != version)
            return Changed();

        var updated = await PendingApplication(userId, version)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BusinessVerificationStatus.Rejected)
                .SetProperty(b => b.RejectionReason, text)
                .SetProperty(b => b.RejectionCount, b => b.RejectionCount + 1)
                .SetProperty(b => b.ReviewedAt, now)
                .SetProperty(b => b.ReviewedByUserId, actorId)
                .SetProperty(b => b.UpdatedAt, now), ct);

        if (updated == 0)
            return Changed();

        audit.Record(ModerationLog.ActionRejectBusiness, ModerationLog.TargetBusiness, userId,
            reason: text,
            payload: new { submittedAt = version, reason = text, rejectionCount = app.RejectionCount + 1 });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }

    // ---- helpers ----

    /// <summary>Строка, на которую ложится решение: ровно та заявка, что видел модератор.</summary>
    private IQueryable<BusinessProfile> PendingApplication(Guid userId, DateTimeOffset submittedAt) =>
        db.BusinessProfiles.Where(b =>
            b.UserId == userId &&
            b.Status == BusinessVerificationStatus.Pending &&
            b.AccountType == AccountType.Business &&
            b.SubmittedAt == submittedAt);

    private BusinessError? Precheck(Guid userId, DateTimeOffset? submittedAt)
    {
        if (submittedAt is null)
            return new BusinessError(StatusCodes.Status400BadRequest,
                BusinessErrorCodes.SubmittedAtRequired,
                "Не передана версия заявки (submittedAt)", Field: "submittedAt");

        // Модератор с собственным магазином не подтверждает сам себя.
        if (userId == currentUser.UserId)
            return new BusinessError(StatusCodes.Status403Forbidden,
                BusinessErrorCodes.SelfReview, "Нельзя принимать решение по собственной заявке");

        return null;
    }

    private static BusinessError NotFound() => new(StatusCodes.Status404NotFound,
        BusinessErrorCodes.ApplicationNotFound, "Заявка не найдена");

    private static BusinessError Changed() => new(StatusCodes.Status409Conflict,
        BusinessErrorCodes.ApplicationChanged,
        "Заявка уже рассмотрена, отозвана или подана заново — обновите список");

    private static BusinessError RegistrationNumberTaken() => new(StatusCodes.Status409Conflict,
        BusinessErrorCodes.RegistrationNumberTaken,
        "Этот регистрационный номер уже подтверждён у другого аккаунта", Field: "registrationNumber");
}
