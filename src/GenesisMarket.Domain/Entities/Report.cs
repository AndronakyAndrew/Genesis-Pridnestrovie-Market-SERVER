using GenesisMarket.Domain.Common;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Жалоба на объект (объявление, пользователь, отзыв). Приём открыт анонимам —
/// мошенничество часто замечает тот, кто ещё не зарегистрирован. Сырой IP не
/// хранится: только HMAC (<see cref="ReporterIpHash"/>) — для дедупликации жалоб
/// анонима и подсчёта независимых репортёров в автоматике модерации.
/// </summary>
public class Report : BaseEntity
{
    public ReportTargetType TargetType { get; set; }

    /// <summary>Id объекта соответствующего <see cref="TargetType"/>.</summary>
    public Guid TargetId { get; set; }

    /// <summary>Автор жалобы. null — анонимная жалоба.</summary>
    public Guid? ReporterId { get; set; }

    /// <summary>
    /// HMAC-SHA256 от IP анонима (hex). null у авторизованных жалоб. Сырой IP не хранится.
    /// Служит ключом дедупликации и «независимости» для анонимных репортёров.
    /// </summary>
    public string? ReporterIpHash { get; set; }

    public ReportReason Reason { get; set; }

    /// <summary>Комментарий репортёра, до 500 символов (CHECK на уровне БД).</summary>
    public string? Comment { get; set; }

    public ReportStatus Status { get; set; } = ReportStatus.New;

    /// <summary>Модератор, закрывший жалобу (Resolved/Rejected).</summary>
    public Guid? ResolvedByUserId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Итог разбора модератором (свободный текст).</summary>
    public string? Resolution { get; set; }
}
