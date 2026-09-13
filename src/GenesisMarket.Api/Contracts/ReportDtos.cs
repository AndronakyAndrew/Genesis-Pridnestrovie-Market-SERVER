using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Жалоба на объект. Доступна анонимам. <c>Comment</c> опционален (до 500 символов).
///
/// Цель задаётся одним из двух способов. Объявление и отзыв — по <see cref="TargetId"/>
/// (их Guid клиенту известны). Пользователь — по <see cref="TargetPublicCode"/>
/// («ID профиля»): Guid продавца наружу больше не отдаётся, и жаловаться на него
/// по Guid стало нечем. Guid для <c>TargetType = User</c> по-прежнему принимается —
/// ради клиентов, которые его ещё помнят.
/// </summary>
public record CreateReportRequest(
    [Required] ReportTargetType TargetType,
    Guid? TargetId,
    [MaxLength(5)] string? TargetPublicCode,
    [Required] ReportReason Reason,
    [MaxLength(500)] string? Comment);

/// <summary>
/// Принятая жалоба. Отдаётся и при создании (201), и при дубликате (200) —
/// в обоих случаях одно и то же представление, без раскрытия внутренней очереди.
/// </summary>
public record ReportResponse(
    Guid Id,
    ReportTargetType TargetType,
    /// <summary>Guid объекта — для объявления и отзыва. Для пользователя null.</summary>
    Guid? TargetId,
    /// <summary>«ID профиля» — только для жалоб на пользователя (иначе null).</summary>
    string? TargetPublicCode,
    ReportReason Reason,
    string? Comment,
    ReportStatus Status,
    DateTimeOffset CreatedAt);
