using System.Text.Json;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

// Рабочее место модератора: журнал действий, реестр пользователей, жалобы.
// Всё — только под policy «Moderator». Контактов (email/телефон) здесь нет нигде:
// они отдаются одной ручкой GET /api/moderation/users/{id}, и каждый её вызов
// пишется в журнал. Сырого IP нет и быть не может — сервер его не хранит.

// ---- общие ----

/// <summary>Кто выполнил действие (модератор/администратор).</summary>
public record ModerationActor(Guid Id, string PublicCode, string DisplayName, UserRole Role);

/// <summary>
/// Короткая ссылка на объект действия/жалобы — чтобы строка журнала или жалобы
/// читалась без второго запроса. Заполнены только поля, осмысленные для типа:
/// у объявления — заголовок, slug, цена, город, миниатюра; у пользователя —
/// имя и «ID профиля»; у жалобы — причина и тип её объекта.
/// </summary>
public record ModerationTargetRef(
    /// <summary>listing | user | report | review | business.</summary>
    string Type,
    Guid Id,
    /// <summary>Заголовок объявления, имя пользователя, текст отзыва (обрезан), название магазина.</summary>
    string Label,
    /// <summary>«ID профиля» пользователя (или владельца объекта).</summary>
    string? PublicCode = null,
    string? Slug = null,
    /// <summary>Ключ миниатюры первого фото (фронт превращает в адрес через /api/img).</summary>
    string? ThumbKey = null,
    decimal? Price = null,
    PriceType? PriceType = null,
    City? City = null,
    Category? Category = null,
    ListingStatus? ListingStatus = null,
    ReportReason? ReportReason = null,
    ReportTargetType? ReportTargetType = null,
    /// <summary>false — объект удалён или не найден (ссылка из журнала пережила объект).</summary>
    bool Exists = true);

// ---- журнал действий ----

/// <summary>
/// Фильтры журнала. Все опциональны. <c>action</c> повторяется в query
/// (<c>?action=listing.approve&amp;action=listing.reject</c>) — фронт сам
/// раскладывает «группы» (одобрения, санкции…) на коды.
/// </summary>
public record ModerationLogQuery
{
    /// <summary>Начало периода включительно (UTC).</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Конец периода не включительно (UTC).</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Только действия этого модератора.</summary>
    public Guid? ActorId { get; init; }

    /// <summary>Коды действий (ModerationLog.Action*). Пусто — все.</summary>
    public string[]? Action { get; init; }

    /// <summary>listing | user | report | business.</summary>
    public string? TargetType { get; init; }

    /// <summary>
    /// Поиск: GUID объекта или модератора, «ID профиля» (5 цифр), иначе — подстрока
    /// заголовка объявления или причины.
    /// </summary>
    public string? Q { get; init; }

    public string? Cursor { get; init; }

    /// <summary>По умолчанию 25, максимум 100.</summary>
    public int? Limit { get; init; }
}

/// <summary>Запись журнала модерации.</summary>
public record ModerationLogItem(
    Guid Id,
    DateTimeOffset CreatedAt,
    ModerationActor Actor,
    string Action,
    string TargetType,
    Guid TargetId,
    ModerationTargetRef? Target,
    string? Reason,
    /// <summary>Снимок решения (JSON как есть). Не содержит PII.</summary>
    JsonElement? Payload,
    /// <summary>Сколько объект ждал решения, секунд. null — не решение или старая запись.</summary>
    int? WaitSeconds);

/// <summary>Страница журнала (новые сверху), курсорная пагинация.</summary>
public record ModerationLogPage(IReadOnlyList<ModerationLogItem> Items, string? NextCursor, bool HasMore);

/// <summary>Период для сводки журнала.</summary>
public record ModerationLogSummaryQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public Guid? ActorId { get; init; }
}

/// <summary>
/// Сводка журнала за период: плитки над таблицей журнала и счётчики вкладок периода.
/// Считается по тем же фильтрам периода/модератора, что и список.
/// </summary>
public record ModerationLogSummary(
    int Total,
    int Approvals,
    int Rejections,
    /// <summary>Возвращено автору на доработку (listing.revise).</summary>
    int Revisions,
    int Bans,
    int Warnings,
    int ReportsResolved,
    int BusinessDecisions,
    /// <summary>Среднее ожидание решения, секунд; null — решений с замером не было.</summary>
    double? AvgWaitSeconds);

// ---- пользователи ----

/// <summary>Вкладки реестра пользователей.</summary>
public enum ModerationUsersTab
{
    All,
    /// <summary>Действующий бан (IsBanned).</summary>
    Banned,
    /// <summary>Есть открытые жалобы на профиль или его объявления.</summary>
    Reported,
    /// <summary>Зарегистрированы за последние 48 часов.</summary>
    New,
    /// <summary>Модераторы и администраторы.</summary>
    Staff,
    /// <summary>Подтверждённый бизнес.</summary>
    Business
}

public record ModerationUsersQuery
{
    public ModerationUsersTab? Tab { get; init; }
    public City? City { get; init; }

    /// <summary>«ID профиля» (5 цифр), @telegram или подстрока имени / названия магазина. Email не ищется.</summary>
    public string? Q { get; init; }

    public string? Cursor { get; init; }

    /// <summary>По умолчанию 25, максимум 100.</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// Строка реестра пользователей. Без email/телефона — они только в журналируемой
/// карточке контактов. <see cref="LastIpPrefix"/> — два октета, ровно то, что
/// пользователь сам видит в своих сессиях.
/// </summary>
public record ModerationUserItem(
    Guid Id,
    string PublicCode,
    string DisplayName,
    string? AvatarUrl,
    City City,
    UserRole Role,
    DateTimeOffset CreatedAt,
    bool EmailVerified,
    bool PhoneVerified,
    bool IsBanned,
    DateTimeOffset? BannedUntil,
    int ActiveListings,
    int ApprovedListings,
    double? AverageRating,
    int ReviewsCount,
    /// <summary>Открытые жалобы (New/InReview) на профиль и на его объявления.</summary>
    int OpenReports,
    DateTimeOffset? LastRejectedAt,
    bool IsVerifiedBusiness,
    string? ShopName,
    DateTimeOffset? LastSeenAt,
    string? LastIpPrefix,
    /// <summary>
    /// Сколько ДРУГИХ аккаунтов входили с того же адреса (сравнение HMAC). Сигнал,
    /// а не доказательство: общий NAT оператора связи даёт совпадения честным людям.
    /// null — в списке не считается (дорого), только в карточке.
    /// </summary>
    int? SharedNetworkAccounts = null);

public record ModerationUsersPage(IReadOnlyList<ModerationUserItem> Items, string? NextCursor, bool HasMore);

/// <summary>Плитки над реестром пользователей.</summary>
public record ModerationUsersSummary(
    int Total,
    int NewLast24h,
    int NewLast48h,
    /// <summary>Есть хотя бы одно активное объявление.</summary>
    int ActiveSellers,
    /// <summary>Открытые жалобы на профили (TargetType = User).</summary>
    int ProfileReportsOpen,
    int ActiveBans,
    int TemporaryBans,
    int PermanentBans);

/// <summary>Связанный по сети аккаунт (тот же HMAC адреса входа).</summary>
public record LinkedAccount(Guid Id, string PublicCode, string DisplayName, bool IsBanned, DateTimeOffset CreatedAt);

/// <summary>Запись истории санкций (из журнала модерации).</summary>
public record SanctionHistoryItem(
    Guid LogId, string Action, DateTimeOffset CreatedAt, ModerationActor Actor, string? Reason,
    /// <summary>Срок бана из снимка решения; null — бессрочно или не бан.</summary>
    DateTimeOffset? Until);

/// <summary>Короткая строка объявления в досье.</summary>
public record DossierListing(
    Guid Id, string Slug, string Title, ListingStatus Status, decimal? Price, PriceType PriceType,
    DateTimeOffset CreatedAt, string? ThumbKey, RejectionReasonCode? RejectionReasonCode);

/// <summary>
/// Досье пользователя для модератора: всё, что нужно для решения о санкции, без
/// контактов. Контакты — отдельной журналируемой ручкой.
/// </summary>
public record ModerationUserDossier(
    ModerationUserItem User,
    string? Description,
    string? TelegramUsername,
    /// <summary>Объявления по статусам: ключи — ListingStatus.</summary>
    IReadOnlyDictionary<string, int> ListingsByStatus,
    IReadOnlyList<DossierListing> RecentListings,
    int ReportsFiled,
    /// <summary>Жалобы пользователя, признанные необоснованными (Rejected).</summary>
    int ReportsFiledRejected,
    int ReportsAgainstTotal,
    /// <summary>Последние (до 5) различающиеся префиксы адресов входа.</summary>
    IReadOnlyList<string> IpPrefixes,
    IReadOnlyList<LinkedAccount> LinkedAccounts,
    IReadOnlyList<SanctionHistoryItem> Sanctions,
    BusinessVerificationStatus? BusinessStatus);

// ---- жалобы ----

/// <summary>Вкладки экрана жалоб.</summary>
public enum ModerationReportsTab
{
    /// <summary>New + InReview.</summary>
    Open,
    New,
    InReview,
    /// <summary>Resolved + Rejected.</summary>
    Closed,
    All
}

public enum ModerationReportsSort
{
    /// <summary>Сначала тяжёлые причины (мошенничество, запрещёнка), внутри — старые.</summary>
    Urgent,
    Oldest,
    Newest
}

public record ModerationReportsQuery
{
    public ModerationReportsTab? Tab { get; init; }
    public ReportTargetType? TargetType { get; init; }
    public ReportReason? Reason { get; init; }
    public ModerationReportsSort? Sort { get; init; }

    /// <summary>Только взятые в работу текущим модератором.</summary>
    public bool? Mine { get; init; }

    /// <summary>GUID жалобы/объекта, «ID профиля» заявителя или подстрока комментария.</summary>
    public string? Q { get; init; }

    public string? Cursor { get; init; }

    /// <summary>По умолчанию 25, максимум 100.</summary>
    public int? Limit { get; init; }
}

/// <summary>Кто подал жалобу. null в строке — аноним.</summary>
public record ReporterRef(Guid Id, string PublicCode, string DisplayName);

public record ModerationReportRow(
    Guid Id,
    ReportReason Reason,
    /// <summary>Тяжесть причины: 3 — мошенничество/запрещёнка, 2 — цена, 1 — прочее.</summary>
    int Severity,
    ReportStatus Status,
    string? Comment,
    DateTimeOffset CreatedAt,
    ReportTargetType TargetType,
    Guid TargetId,
    ModerationTargetRef? Target,
    ReporterRef? Reporter,
    ModerationActor? Assignee,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? ResolvedAt,
    string? Resolution);

public record ModerationReportsPage(IReadOnlyList<ModerationReportRow> Items, string? NextCursor, bool HasMore);

/// <summary>Счётчики вкладок и SLA экрана жалоб.</summary>
public record ModerationReportsSummary(
    int All,
    int New,
    int InReview,
    int Closed,
    int OpenListings,
    int OpenUsers,
    int OpenReviews,
    /// <summary>Среднее время от подачи до закрытия за 7 дней, минут. null — закрытых не было.</summary>
    double? AvgReactionMinutes);

/// <summary>Статистика заявителя — оценка надёжности его жалоб.</summary>
public record ReporterStats(
    Guid Id, string PublicCode, string DisplayName, DateTimeOffset CreatedAt, bool PhoneVerified,
    int ReportsTotal, int ReportsResolved, int ReportsRejected);

/// <summary>Снимок объявления — объекта жалобы. Текст сырой: модератору нужен оригинал.</summary>
public record ReportedListing(
    Guid Id, string Slug, string Title, string Description, decimal? Price, PriceType PriceType,
    Category Category, int SubcategoryId, City City, string? District, ListingStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt, int ViewsCount, bool IsDeleted,
    IReadOnlyList<string> ThumbKeys, ModerationUserItem Owner);

/// <summary>Снимок отзыва — объекта жалобы.</summary>
public record ReportedReview(
    Guid Id, int Rating, string Text, bool IsHidden, DateTimeOffset CreatedAt, Guid ListingId,
    string AuthorPublicCode, string AuthorName, string SubjectPublicCode);

/// <summary>Карточка жалобы: сама жалоба, заявитель и снимок объекта (одно из трёх).</summary>
public record ModerationReportDetail(
    ModerationReportRow Report,
    ReporterStats? ReporterStats,
    ReportedListing? Listing,
    ModerationUserItem? User,
    ReportedReview? Review,
    /// <summary>Сколько ещё открытых жалоб на тот же объект.</summary>
    int OtherOpenReportsOnTarget);
