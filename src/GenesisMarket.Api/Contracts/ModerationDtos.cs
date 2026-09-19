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
    /// <summary>«ID профиля» владельца — им модератор оперирует вместо GUID.</summary>
    string OwnerPublicCode,
    string OwnerDisplayName,
    bool OwnerIsBanned,
    IReadOnlyList<ModerationReportItem> OpenReports,
    /// <summary>
    /// Момент постановки в очередь модерации. Вместе со <see cref="Status"/> задаёт режим:
    /// заполнен + PendingReview — премодерация (в каталоге нет); заполнен + Active —
    /// постмодерация (уже на витрине); null — проверка не требуется.
    /// </summary>
    DateTimeOffset? ReviewQueuedAt = null,
    /// <summary>Момент первого одобрения. null — объявление ещё ни разу не подтверждено.</summary>
    DateTimeOffset? ApprovedAt = null,
    /// <summary>Сколько объявлений автора уже одобрено — контекст для решения модератора.</summary>
    int OwnerApprovedListings = 0,
    /// <summary>Когда автор последний раз получал отказ. null — отказов не было.</summary>
    DateTimeOffset? OwnerLastRejectedAt = null,
    /// <summary>Досье автора одной строкой (без контактов) — со счётчиками и сетевым сигналом.</summary>
    ModerationUserItem? Owner = null,
    /// <summary>Модератор, на которого назначено объявление в очереди.</summary>
    ModerationActor? Assignee = null,
    DateTimeOffset? ReviewAssignedAt = null,
    /// <summary>Возвращено на доработку (черновик с причиной). null — не возвращалось.</summary>
    DateTimeOffset? RevisionRequestedAt = null,
    /// <summary>Причина последнего отказа или доработки — модератору видна всегда.</summary>
    RejectionReasonCode? RejectionReasonCode = null,
    string? RejectionComment = null,
    /// <summary>
    /// Медиана фиксированных цен подкатегории за окно Moderation:MarketWindowDays.
    /// null — выборка меньше Moderation:MarketMinSample. Подсказка, не правило.
    /// </summary>
    decimal? MarketMedianPrice = null,
    int MarketSampleSize = 0,
    /// <summary>Норматив ожидания в очереди, минут (для «просрочено SLA»).</summary>
    int ReviewSlaMinutes = 0);

/// <summary>Открытая жалоба в карточке объявления.</summary>
public record ModerationReportItem(
    Guid Id,
    ReportReason Reason,
    string? Comment,
    ReportStatus Status,
    Guid? ReporterId,
    /// <summary>«ID профиля» заявителя. null — жалоба от анонима.</summary>
    string? ReporterPublicCode,
    DateTimeOffset CreatedAt);

/// <summary>
/// Отклонение объявления модератором. Причина уходит автору через outbox и,
/// в отличие от прежнего поведения, сохраняется на самом объявлении.
///
/// Код причины обязателен: общее «объявление отклонено» не говорит автору, что
/// чинить, и он публикует то же самое заново. Комментарий обязателен только при
/// <see cref="RejectionReasonCode.Other"/> — там код сам по себе не объясняет ничего.
/// </summary>
public record RejectListingRequest(
    [Required] RejectionReasonCode Reason,
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
    /// <summary>«ID профиля» — ключ, по которому эту карточку и находят.</summary>
    string PublicCode,
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
    /// <summary>Премодерация: объявления, которых из-за очереди ещё нет в каталоге.</summary>
    int PendingListings,
    int OpenReports,
    /// <summary>Всё, что ждёт модератора: премодерация + постмодерация + открытые жалобы.</summary>
    int QueueTotal,
    int ActionsToday,
    int ActionsThisWeek,
    int BansToday,
    /// <summary>Постмодерация: объявления уже в каталоге, но ждут выборочной проверки.</summary>
    int PostReviewListings = 0,
    // ---- дашборд рабочего места (добавлены в конец: старый клиент их не читает) ----
    /// <summary>Возвращены автору на доработку и ещё не переопубликованы.</summary>
    int RevisionListings = 0,
    /// <summary>Жалобы, взятые в работу (InReview).</summary>
    int InReviewReports = 0,
    int ApprovalsToday = 0,
    int RejectionsToday = 0,
    int RevisionsToday = 0,
    int ActionsYesterday = 0,
    int ActiveBans = 0,
    int PendingBusinessApplications = 0,
    /// <summary>Самое старое объявление в очереди — момент постановки. null — очередь пуста.</summary>
    DateTimeOffset? OldestQueuedAt = null,
    /// <summary>Объявлений в очереди дольше норматива <see cref="ReviewSlaMinutes"/>.</summary>
    int OverdueListings = 0,
    int ReviewSlaMinutes = 0,
    /// <summary>Среднее ожидание решения сегодня, секунд. null — решений с замером не было.</summary>
    double? AvgWaitSecondsToday = null,
    /// <summary>Доля решений сегодня в пределах норматива, %. null — решений не было.</summary>
    double? SlaPercentToday = null,
    /// <summary>
    /// Решения по объявлениям за последние 12 часов по часам: [0] — 12 часов назад,
    /// [11] — текущий час. Не привязано к часовому поясу — «последние N часов».
    /// </summary>
    IReadOnlyList<int>? DecisionsLast12h = null);

/// <summary>Результат простого действия модератора (approve/resolve/ban/unban).</summary>
public record ModerationActionResult(string Message);
