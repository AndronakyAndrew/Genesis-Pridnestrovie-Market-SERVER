namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Журнал действий модератора. Таблица ТОЛЬКО на добавление: ни UPDATE, ни DELETE
/// по ней в коде нет. Пишется каждое действие модератора (одобрение/отклонение
/// объявления, разбор жалобы, бан/разбан) и каждый просмотр чувствительных данных
/// пользователя (email/телефон). Служит неизменяемым аудит-следом для разбора
/// злоупотреблений правами модерации.
/// </summary>
public class ModerationLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Модератор, выполнивший действие (из claim JWT).</summary>
    public required Guid ActorId { get; set; }

    /// <summary>Код действия — одна из констант <see cref="ModerationLog"/> (см. ниже).</summary>
    public required string Action { get; set; }

    /// <summary>Тип объекта действия: <c>listing</c>, <c>user</c>, <c>report</c>.</summary>
    public required string TargetType { get; set; }

    public required Guid TargetId { get; set; }

    /// <summary>Причина (для reject/ban) — свободный текст, до 500 символов.</summary>
    public string? Reason { get; set; }

    /// <summary>Полезная нагрузка действия в JSON (снимок решения). Не PII.</summary>
    public string? PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Сколько объект ждал решения, секунд: для объявления — от постановки в очередь,
    /// для жалобы — от подачи. Заполняется только у решений (approve/reject/revise/
    /// resolve), по нему считается среднее время реакции. null у прочих действий и
    /// у записей, сделанных до появления поля.
    /// </summary>
    public int? WaitSeconds { get; set; }

    // ---- Коды действий (Action) ----
    public const string TargetListing = "listing";
    public const string TargetUser = "user";
    public const string TargetReport = "report";

    /// <summary>Заявка на подтверждение бизнеса. TargetId — Id пользователя (PK business_profiles).</summary>
    public const string TargetBusiness = "business";

    public const string ActionApproveListing = "listing.approve";
    public const string ActionRejectListing = "listing.reject";

    /// <summary>
    /// Вернуть автору на доработку: объявление уходит в черновик с причиной. В отличие
    /// от отказа, не фиксирует <c>users.LastRejectedAt</c> и не закрывает автопубликацию.
    /// </summary>
    public const string ActionReviseListing = "listing.revise";

    /// <summary>Объявление в очереди назначено модератору («Назначить на меня»).</summary>
    public const string ActionAssignListing = "listing.assign";

    /// <summary>Назначение снято — объявление вернулось в общую очередь.</summary>
    public const string ActionUnassignListing = "listing.unassign";
    public const string ActionResolveReport = "report.resolve";

    /// <summary>Модератор взял жалобу в работу (New → InReview, назначен на себя).</summary>
    public const string ActionTakeReport = "report.take";
    public const string ActionBanUser = "user.ban";
    public const string ActionUnbanUser = "user.unban";

    /// <summary>Предупреждение пользователю — санкция без бана.</summary>
    public const string ActionWarnUser = "user.warn";
    public const string ActionApproveBusiness = "business.approve";
    public const string ActionRejectBusiness = "business.reject";

    /// <summary>Просмотр контактных данных пользователя (email/телефон) — чувствительное чтение.</summary>
    public const string ActionViewUserContacts = "user.view_contacts";
}
