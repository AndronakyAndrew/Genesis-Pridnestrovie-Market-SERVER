using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>Вид элемента очереди модерации.</summary>
public enum ModerationQueueKind
{
    /// <summary>Объявление на премодерации (PendingReview).</summary>
    Listing,
    /// <summary>Открытая жалоба (Status = New).</summary>
    Report
}

/// <summary>
/// Фильтры очереди модерации (все опциональны, биндятся из query-string).
/// <c>type</c> — <c>listing</c>|<c>report</c>; <c>reason</c> — причина жалобы
/// (сужает выдачу до жалоб); <c>priority</c> — минимальный приоритет автофлага.
/// </summary>
public record ModerationQueueQuery
{
    /// <summary>Ограничить вид элементов: listing | report.</summary>
    public ModerationQueueKind? Type { get; init; }

    /// <summary>Только жалобы с этой причиной (объявления при этом исключаются).</summary>
    public ReportReason? Reason { get; init; }

    /// <summary>Минимальный приоритет очереди (автофлаги имеют priority > 0).</summary>
    public int? Priority { get; init; }

    /// <summary>Курсор предыдущей страницы (opaque, base64url).</summary>
    public string? Cursor { get; init; }

    /// <summary>Размер страницы. По умолчанию 20, максимум 50.</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// Элемент очереди модерации — единое представление для объявлений на премодерации
/// и открытых жалоб. Отсортированы: сначала автофлаги (по убыванию приоритета),
/// затем по дате (сначала старые — FIFO разбора).
/// </summary>
public record ModerationQueueItem(
    ModerationQueueKind Kind,
    Guid Id,
    int Priority,
    DateTimeOffset CreatedAt,
    string Status,
    /// <summary>Заголовок объявления (для Kind = Listing).</summary>
    string? Title = null,
    /// <summary>Тип объекта жалобы (для Kind = Report).</summary>
    ReportTargetType? ReportTargetType = null,
    /// <summary>Id объекта жалобы (для Kind = Report).</summary>
    Guid? ReportTargetId = null,
    /// <summary>Причина жалобы (для Kind = Report).</summary>
    ReportReason? Reason = null);

/// <summary>Страница очереди модерации с курсорной пагинацией.</summary>
public record ModerationQueuePage(
    IReadOnlyList<ModerationQueueItem> Items,
    string? NextCursor,
    bool HasMore);

/// <summary>
/// Полная карточка объявления для модератора — включая скрытые от публики поля
/// (владелец, статус даже у скрытого/удалённого, приоритет очереди, метка удаления)
/// и открытые жалобы по этому объявлению.
/// </summary>
public record ModerationListingCard(
    Guid Id,
    string Slug,
    string Title,
    string Description,
    decimal? Price,
    PriceType PriceType,
    Category Category,
    int SubcategoryId,
    City City,
    string? District,
    Condition Condition,
    ListingStatus Status,
    int ViewsCount,
    int FavoritesCount,
    int ModerationPriority,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? DeletedAt,
    Guid OwnerId,
    string OwnerDisplayName,
    bool OwnerIsBanned,
    IReadOnlyList<ModerationReportItem> OpenReports);

/// <summary>Открытая жалоба в карточке объявления.</summary>
public record ModerationReportItem(
    Guid Id,
    ReportReason Reason,
    string? Comment,
    ReportStatus Status,
    Guid? ReporterId,
    DateTimeOffset CreatedAt);

/// <summary>Отклонение объявления модератором. Причина уходит автору через outbox.</summary>
public record RejectListingRequest(
    [Required] ReportReason Reason,
    [MaxLength(500)] string? Comment);

/// <summary>Разбор жалобы: закрыть как Resolved (нарушение подтверждено) или Rejected (жалоба неверна).</summary>
public record ResolveReportRequest(
    [Required] ReportStatus Status,
    [MaxLength(1000)] string? Resolution);

/// <summary>Бан пользователя. <c>Until</c> = null — бессрочно.</summary>
public record BanUserRequest(
    [Required][MaxLength(500)] string Reason,
    DateTimeOffset? Until);

/// <summary>
/// Контактные данные пользователя — САМАЯ чувствительная ручка. Каждый вызов
/// пишется в <c>moderation_logs</c>. Наружу только модератору (policy Moderator).
/// </summary>
public record ModerationUserContacts(
    Guid Id,
    string Email,
    string? PhoneE164,
    bool EmailVerified,
    bool PhoneVerified,
    UserRole Role,
    bool IsBanned,
    DateTimeOffset? BannedUntil,
    DateTimeOffset CreatedAt);

/// <summary>Счётчики очереди и активности модерации за сегодня/неделю.</summary>
public record ModerationStats(
    int PendingListings,
    int OpenReports,
    int QueueTotal,
    int ActionsToday,
    int ActionsThisWeek,
    int BansToday);

/// <summary>Результат простого действия модератора (approve/resolve/ban/unban).</summary>
public record ModerationActionResult(string Message);
