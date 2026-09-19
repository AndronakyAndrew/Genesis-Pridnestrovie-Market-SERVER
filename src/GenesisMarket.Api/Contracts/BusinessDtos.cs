using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Сохранение реквизитов (<c>PUT /api/account/business</c>) — оно же включение
/// бизнес-режима. ФИКСИРОВАННЫЙ набор полей: статуса, причины отказа, дат и
/// автора решения здесь нет и быть не может — клиент не выставляет ни Verified,
/// ни Rejected ни при каких условиях.
///
/// Все поля обязательны; валидирует BusinessDetailsRequestValidator, ошибки —
/// ValidationProblemDetails: <c>errors.{поле}</c> — текст, <c>codes.{поле}</c> —
/// машинный код (<see cref="Business.BusinessErrorCodes"/>) для подсветки поля.
/// <see cref="LegalForm"/> nullable намеренно: иначе пропущенное поле молча
/// превратилось бы в первый член enum (ИП).
/// </summary>
public record SaveBusinessDetailsRequest(
    string? ShopName,
    LegalForm? LegalForm,
    string? RegistrationNumber,
    string? PickupAddress);

/// <summary>Реквизиты в ответе владельцу и модератору.</summary>
public record BusinessDetailsDto(
    string ShopName,
    LegalForm LegalForm,
    string RegistrationNumber,
    string PickupAddress);

/// <summary>
/// Состояние бизнес-режима для владельца (<c>GET /api/account/business</c>).
///
/// <see cref="CanEdit"/>, <see cref="CanSubmit"/> и <see cref="NextSubmitAllowedAt"/>
/// считает сервер — клиент правила переходов не дублирует. Автор решения
/// (модератор) владельцу не отдаётся.
/// </summary>
public record BusinessAccountResponse(
    AccountType AccountType,
    BusinessVerificationStatus Status,
    /// <summary>null — реквизиты ещё ни разу не сохранялись.</summary>
    BusinessDetailsDto? Details,
    /// <summary>Причина отказа. Заполнена только в статусе Rejected.</summary>
    string? RejectionReason,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ReviewedAt,
    /// <summary>Реквизиты можно править (не Pending).</summary>
    bool CanEdit,
    /// <summary>Заявку можно подать прямо сейчас (с учётом паузы после отказа).</summary>
    bool CanSubmit,
    /// <summary>Раньше этого момента подача вернёт 429. null — ограничения нет.</summary>
    DateTimeOffset? NextSubmitAllowedAt,
    /// <summary>
    /// true только в ответе на PUT, если правка сняла подтверждение (Verified → None):
    /// клиенту стоит сказать, что бейдж вернётся после повторной проверки.
    /// </summary>
    bool VerificationReset = false);

/// <summary>
/// Публичные сведения о подтверждённом бизнесе — в профиле продавца.
/// Появляются ТОЛЬКО у подтверждённого бизнеса: реквизиты, которые модератор
/// не проверял (или которые принадлежат вернувшемуся в Private), публичными не становятся.
/// </summary>
public record PublicBusinessInfo(
    string ShopName,
    LegalForm LegalForm,
    string RegistrationNumber,
    string PickupAddress);

// ---- модератор ----

/// <summary>Фильтры списка заявок. По умолчанию — ждущие решения.</summary>
public record BusinessApplicationsQuery
{
    /// <summary>Pending (по умолчанию) | Verified | Rejected. None — не заявка, 400.</summary>
    public BusinessVerificationStatus? Status { get; init; }

    /// <summary>Курсор предыдущей страницы (opaque, base64url).</summary>
    public string? Cursor { get; init; }

    /// <summary>Размер страницы. По умолчанию 20, максимум 50.</summary>
    public int? Limit { get; init; }
}

/// <summary>Заявка на подтверждение бизнеса — всё, что нужно модератору для решения.</summary>
public record BusinessApplicationItem(
    Guid UserId,
    /// <summary>«ID профиля» заявителя — им модератор оперирует вместо GUID.</summary>
    string PublicCode,
    string DisplayName,
    BusinessDetailsDto Details,
    AccountType AccountType,
    BusinessVerificationStatus Status,
    /// <summary>
    /// Момент подачи — он же версия заявки: его нужно вернуть в approve/reject,
    /// чтобы решение не легло на заявку, поданную заново после просмотра.
    /// </summary>
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedByUserId,
    string? RejectionReason,
    /// <summary>Сколько раз заявку уже отклоняли.</summary>
    int RejectionCount,
    bool EmailVerified,
    bool PhoneVerified);

/// <summary>Страница заявок с курсорной пагинацией (FIFO по моменту подачи).</summary>
public record BusinessApplicationsPage(
    IReadOnlyList<BusinessApplicationItem> Items,
    string? NextCursor,
    bool HasMore);

/// <summary>Одобрение заявки. <see cref="SubmittedAt"/> — версия заявки из списка.</summary>
public record ApproveBusinessRequest(DateTimeOffset? SubmittedAt);

/// <summary>Отклонение заявки. Причина обязательна: её видит пользователь, чтобы исправить данные.</summary>
public record RejectBusinessRequest(DateTimeOffset? SubmittedAt, string? Reason);
