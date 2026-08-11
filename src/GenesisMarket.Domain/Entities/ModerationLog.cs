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

    // ---- Коды действий (Action) ----
    public const string TargetListing = "listing";
    public const string TargetUser = "user";
    public const string TargetReport = "report";

    public const string ActionApproveListing = "listing.approve";
    public const string ActionRejectListing = "listing.reject";
    public const string ActionResolveReport = "report.resolve";
    public const string ActionBanUser = "user.ban";
    public const string ActionUnbanUser = "user.unban";

    /// <summary>Просмотр контактных данных пользователя (email/телефон) — чувствительное чтение.</summary>
    public const string ActionViewUserContacts = "user.view_contacts";
}
