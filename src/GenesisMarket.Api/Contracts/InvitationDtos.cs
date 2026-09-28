using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

// ─── Админка: мастер и список ─────────────────────────────────────────────────

/// <summary>Шаг 1 «Магазин».</summary>
public record InvitationShopRequest(
    [Required, StringLength(60, MinimumLength = 2)] string ShopName,
    [Required, MaxLength(40)] string City,
    [Required, MaxLength(40)] string Category,
    [MaxLength(1000)] string? InternalNote);

/// <summary>Шаг 2: черновик товара. Правила — те же, что у объявления.</summary>
public record InvitationDraftRequest(
    [Required] string Title,
    [Required] string Description,
    PriceType PriceType,
    decimal? Price,
    [Required, MaxLength(40)] string Category,
    int SubcategoryId,
    Condition Condition = Condition.NotApplicable);

public record InvitationPhotoResponse(Guid Id, int SortOrder, int Width, int Height, string Url, string ThumbUrl);

public record InvitationDraftResponse(
    Guid Id,
    string Title,
    string Description,
    PriceType PriceType,
    decimal? Price,
    string Category,
    int SubcategoryId,
    Condition Condition,
    int SortOrder,
    IReadOnlyList<InvitationPhotoResponse> Photos,
    /// <summary>Объявление после принятия (null — ещё черновик приглашения).</summary>
    Guid? ListingId,
    DateTimeOffset? UpdatedAt);

/// <summary>Полная карточка приглашения для сотрудника (мастер).</summary>
public record InvitationDetailResponse(
    Guid Id,
    string ShopName,
    string City,
    string Category,
    string? InternalNote,
    InvitationStatus Status,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LinkIssuedAt,
    int LinkVersion,
    Guid CreatedByUserId,
    string? CreatedByPublicCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AcceptedAt,
    IReadOnlyList<InvitationDraftResponse> Drafts,
    /// <summary>Можно ли ещё менять магазин и черновики.</summary>
    bool Editable);

/// <summary>
/// Выпущенная ссылка. Токен показывается ТОЛЬКО здесь: в БД лежит хеш, и повторно
/// ссылку получить нельзя — только выпустить новую взамен.
/// </summary>
public record InvitationLinkResponse(string Url, DateTimeOffset ExpiresAt, int LinkVersion);

public record InvitationListItem(
    Guid Id,
    string ShopName,
    string City,
    InvitationStatus Status,
    DateTimeOffset? ExpiresAt,
    Guid CreatedByUserId,
    string? CreatedByPublicCode,
    string? CreatedByName,
    int DraftsCount,
    int PublishedCount,
    DateTimeOffset CreatedAt);

public record InvitationListPage(IReadOnlyList<InvitationListItem> Items, string? NextCursor);

public record InvitationListQuery(
    InvitationStatus? Status,
    /// <summary>true — только мои (для владельца площадки; остальные всегда видят только свои).</summary>
    bool? Mine,
    string? Cursor,
    int? Limit);

// ─── Страница приглашения для владельца (по токену) ───────────────────────────

public record InvitationPreviewDraft(string Title, string? ThumbUrl);

/// <summary>
/// Что видит владелец по ссылке. Внутренней заметки и данных сотрудника здесь нет.
/// </summary>
public record InvitationPublicResponse(
    string ShopName,
    string City,
    string CityName,
    DateTimeOffset ExpiresAt,
    int DraftsCount,
    IReadOnlyList<InvitationPreviewDraft> Drafts);

/// <summary>Регистрация по ссылке: только email; имя продавца — название магазина.</summary>
public record InvitationSignupRequest([Required, EmailAddress, MaxLength(256)] string Email);

public record InvitationAcceptResponse(int DraftsCount, string Message);

// ─── Кабинет владельца ────────────────────────────────────────────────────────

public record InvitedDraftItem(
    Guid ListingId,
    /// <summary>Адрес объявления — для перехода к обычному редактированию.</summary>
    string Slug,
    string Title,
    string? ThumbUrl,
    PriceType PriceType,
    decimal? Price,
    string Category,
    ListingStatus Status,
    string ShopName,
    DateTimeOffset CreatedAt);

public record InvitedDraftsResponse(
    IReadOnlyList<InvitedDraftItem> Items,
    /// <summary>Текущая версия текста согласия — её клиент отправляет при публикации.</summary>
    string ConsentVersion,
    bool HasPassword);

/// <summary>
/// Публикация выбранных черновиков. Без согласия (или с устаревшей версией текста)
/// сервер отклоняет запрос целиком.
/// </summary>
public record PublishInvitedDraftsRequest(
    [Required, MinLength(1), MaxLength(50)] IReadOnlyList<Guid> ListingIds,
    bool ConsentAccepted,
    [Required, MaxLength(40)] string ConsentVersion);

public record PublishInvitedResult(Guid ListingId, ListingStatus? Status, string? Error);

public record PublishInvitedDraftsResponse(IReadOnlyList<PublishInvitedResult> Results);

/// <summary>Установить пароль аккаунту, созданному без него (по приглашению).</summary>
public record SetPasswordRequest([Required] string NewPassword);
