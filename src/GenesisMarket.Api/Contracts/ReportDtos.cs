using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Жалоба на объект. Доступна анонимам. <c>Comment</c> опционален (до 500 символов).
/// </summary>
public record CreateReportRequest(
    [Required] ReportTargetType TargetType,
    [Required] Guid TargetId,
    [Required] ReportReason Reason,
    [MaxLength(500)] string? Comment);

/// <summary>
/// Принятая жалоба. Отдаётся и при создании (201), и при дубликате (200) —
/// в обоих случаях одно и то же представление, без раскрытия внутренней очереди.
/// </summary>
public record ReportResponse(
    Guid Id,
    ReportTargetType TargetType,
    Guid TargetId,
    ReportReason Reason,
    string? Comment,
    ReportStatus Status,
    DateTimeOffset CreatedAt);
