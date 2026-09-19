using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Бизнес-режим пользователя: реквизиты и их проверка (1:1 с <see cref="User"/>,
/// PK = FK на users.Id, как у <see cref="Profile"/>).
///
/// Отдельная таблица, а не колонки в users: у реквизитов свой жизненный цикл
/// (подача → решение модератора) с собственными инвариантами, которые держат
/// CHECK-констрейнты этой строки; а users читается на каждом запросе
/// (снимок SecurityStampValidator) и расти ради редкого режима не должен.
///
/// Строки нет ⇒ аккаунт частный. Строка есть ⇒ реквизиты заполнены целиком
/// (все поля NOT NULL): «Business без реквизитов» не представим в схеме.
/// После возврата в Private строка остаётся — повторное включение не требует
/// вводить всё заново.
///
/// Переходы пользователя — методы ниже. Решения модератора (Verified/Rejected)
/// здесь намеренно не представлены: их выполняет BusinessVerificationService
/// условным UPDATE (compare-and-set по статусу и моменту подачи), чтобы решение
/// относилось ровно к той заявке, которую видел модератор.
/// </summary>
public class BusinessProfile
{
    /// <summary>PK и одновременно FK на users.Id.</summary>
    public Guid UserId { get; private set; }
    public User? User { get; private set; }

    public AccountType AccountType { get; private set; }

    /// <summary>Публичное название магазина — отдаётся для бейджа, только когда бизнес подтверждён.</summary>
    public string ShopName { get; private set; } = null!;

    public LegalForm LegalForm { get; private set; }

    /// <summary>Регистрационный номер ЕГРЮЛ/ЕГРИП ПМР в нормализованном виде (см. RegistrationNumber.Normalize).</summary>
    public string RegistrationNumber { get; private set; } = null!;

    /// <summary>Фактический адрес точки самовывоза.</summary>
    public string PickupAddress { get; private set; } = null!;

    public BusinessVerificationStatus Status { get; private set; }

    /// <summary>Причина последнего отклонения. Заполнена при <see cref="BusinessVerificationStatus.Rejected"/>.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>Сколько раз заявку отклоняли. Растит паузу перед повторной подачей.</summary>
    public int RejectionCount { get; private set; }

    /// <summary>Момент последней подачи на проверку. Сохраняется и после решения.</summary>
    public DateTimeOffset? SubmittedAt { get; private set; }

    /// <summary>Момент последнего решения модератора.</summary>
    public DateTimeOffset? ReviewedAt { get; private set; }

    /// <summary>Модератор, принявший последнее решение. Наружу пользователю не отдаётся.</summary>
    public Guid? ReviewedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    private BusinessProfile() { }   // EF

    /// <summary>Подтверждённый бизнес: режим включён и проверка пройдена. Основание для бейджа.</summary>
    public bool IsVerifiedBusiness =>
        AccountType == AccountType.Business && Status == BusinessVerificationStatus.Verified;

    /// <summary>Реквизиты можно править всегда, кроме периода рассмотрения заявки.</summary>
    public bool CanEditDetails => Status != BusinessVerificationStatus.Pending;

    /// <summary>
    /// Первое сохранение реквизитов — оно же включение бизнес-режима.
    /// Реквизиты к этому моменту уже провалидированы.
    /// </summary>
    public static BusinessProfile Create(Guid userId, BusinessDetails details, DateTimeOffset now) => new()
    {
        UserId = userId,
        AccountType = AccountType.Business,
        ShopName = details.ShopName,
        LegalForm = details.LegalForm,
        RegistrationNumber = details.RegistrationNumber,
        PickupAddress = details.PickupAddress,
        Status = BusinessVerificationStatus.None,
        CreatedAt = now
    };

    /// <summary>
    /// Сохранить реквизиты и (снова) включить бизнес-режим.
    /// Правка подтверждённых реквизитов снимает подтверждение: модератор проверял
    /// другие данные, и бейдж на новых держаться не должен. Правка после отказа
    /// статус не меняет — причина отказа остаётся видна до повторной подачи.
    /// </summary>
    /// <returns>true, если правка сняла подтверждение (Verified → None).</returns>
    public bool UpdateDetails(BusinessDetails details, DateTimeOffset now)
    {
        if (!CanEditDetails)
            throw new InvalidOperationException("Реквизиты на проверке — правка запрещена.");

        var changed = details != CurrentDetails;
        var verificationReset = changed && Status == BusinessVerificationStatus.Verified;

        ShopName = details.ShopName;
        LegalForm = details.LegalForm;
        RegistrationNumber = details.RegistrationNumber;
        PickupAddress = details.PickupAddress;
        AccountType = AccountType.Business;

        if (verificationReset)
            Status = BusinessVerificationStatus.None;

        UpdatedAt = now;
        return verificationReset;
    }

    /// <summary>Отправить на проверку: None | Rejected → Pending.</summary>
    public void Submit(DateTimeOffset now)
    {
        if (AccountType != AccountType.Business)
            throw new InvalidOperationException("Бизнес-режим выключен.");
        if (Status is not (BusinessVerificationStatus.None or BusinessVerificationStatus.Rejected))
            throw new InvalidOperationException($"Подать заявку нельзя в статусе {Status}.");

        Status = BusinessVerificationStatus.Pending;
        // Прошлая причина отказа относится к прошлой заявке; в журнале модерации она сохранена.
        RejectionReason = null;
        SubmittedAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Вернуться в Private. Подтверждение снимается; заявка на рассмотрении отзывается.
    /// Отказ остаётся отказом — иначе выключение/включение режима обнуляло бы
    /// паузу перед повторной подачей. Реквизиты сохраняются. Идемпотентно.
    /// </summary>
    public void Disable(DateTimeOffset now)
    {
        if (AccountType == AccountType.Private)
            return;

        AccountType = AccountType.Private;
        if (Status is BusinessVerificationStatus.Verified or BusinessVerificationStatus.Pending)
            Status = BusinessVerificationStatus.None;

        UpdatedAt = now;
    }

    public BusinessDetails CurrentDetails => new(ShopName, LegalForm, RegistrationNumber, PickupAddress);
}

/// <summary>Реквизиты бизнеса — уже нормализованные и провалидированные.</summary>
public sealed record BusinessDetails(
    string ShopName,
    LegalForm LegalForm,
    string RegistrationNumber,
    string PickupAddress);
