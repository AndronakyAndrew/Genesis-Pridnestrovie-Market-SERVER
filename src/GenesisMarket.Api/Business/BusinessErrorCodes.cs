namespace GenesisMarket.Api.Business;

/// <summary>
/// Машинные коды ошибок бизнес-режима. Приходят в ProblemDetails:
/// <c>code</c> — у любой ошибки, <c>codes.{поле}</c> — у ошибок конкретного поля
/// (вместе с <c>errors.{поле}</c> — текстом для пользователя). По коду клиент
/// подсвечивает поле и выбирает текст; по-русски <c>title</c> — запасной вариант.
/// Коды — контракт: переименование ломает клиента.
/// </summary>
public static class BusinessErrorCodes
{
    // ---- поля реквизитов (400) ----
    public const string ShopNameRequired = "shop_name_required";
    public const string ShopNameLength = "shop_name_length";
    public const string ShopNameContainsContacts = "shop_name_contains_contacts";
    public const string LegalFormRequired = "legal_form_required";
    public const string RegistrationNumberRequired = "registration_number_required";
    public const string RegistrationNumberFormat = "registration_number_format";
    public const string PickupAddressRequired = "pickup_address_required";
    public const string PickupAddressLength = "pickup_address_length";
    public const string PickupAddressContainsContacts = "pickup_address_contains_contacts";

    /// <summary>Номер уже подтверждён у другого аккаунта (409, поле registrationNumber).</summary>
    public const string RegistrationNumberTaken = "registration_number_taken";

    // ---- переходы пользователя ----
    /// <summary>Заявка на проверке — реквизиты только на чтение (409).</summary>
    public const string DetailsLocked = "details_locked";
    /// <summary>Режим не включён: сначала сохранить реквизиты (409).</summary>
    public const string BusinessModeDisabled = "business_mode_disabled";
    public const string AlreadyPending = "already_pending";
    public const string AlreadyVerified = "already_verified";
    /// <summary>Подача раньше разрешённого момента (429, в ответе <c>retryAt</c>).</summary>
    public const string ResubmitCooldown = "resubmit_cooldown";
    /// <summary>Подавать заявку может только аккаунт с подтверждённой почтой (403).</summary>
    public const string EmailNotVerified = "email_not_verified";
    /// <summary>Состояние изменилось параллельным запросом — перечитать и повторить (409).</summary>
    public const string ConcurrentChange = "concurrent_change";

    // ---- модератор ----
    public const string ApplicationNotFound = "application_not_found";
    /// <summary>Заявка уже решена, отозвана или подана заново после просмотра (409).</summary>
    public const string ApplicationChanged = "application_changed";
    public const string SubmittedAtRequired = "submitted_at_required";
    public const string RejectionReasonRequired = "rejection_reason_required";
    public const string RejectionReasonLength = "rejection_reason_length";
    /// <summary>Модератор не решает собственную заявку (403).</summary>
    public const string SelfReview = "self_review";
    public const string InvalidStatusFilter = "invalid_status_filter";
    public const string InvalidCursor = "invalid_cursor";
}

/// <summary>Ошибка операции бизнес-режима: HTTP-статус, машинный код, текст, поле (если есть).</summary>
public sealed record BusinessError(
    int Status,
    string Code,
    string Title,
    string? Field = null,
    DateTimeOffset? RetryAt = null);
