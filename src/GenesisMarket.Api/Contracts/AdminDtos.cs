using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>Сотрудник в списке «Сотрудники».</summary>
public record StaffMemberResponse(
    Guid UserId,
    string PublicCode,
    string DisplayName,
    string Email,
    UserRole Role,
    bool TwoFactorEnabled,
    bool IsBanned,
    DateTimeOffset GrantedAt,
    Guid? GrantedByUserId,
    string? GrantedByPublicCode);

/// <summary>
/// Результат поиска по email. Роль и 2FA — чтобы в диалоге подтверждения было видно,
/// откуда и куда меняется роль и что ждёт человека после назначения.
/// </summary>
public record StaffLookupResponse(
    Guid UserId,
    string PublicCode,
    string DisplayName,
    string Email,
    UserRole Role,
    bool TwoFactorEnabled,
    bool IsBanned,
    DateTimeOffset CreatedAt);

/// <summary>
/// Смена роли. <paramref name="ExpectedRole"/> — роль, показанная в диалоге
/// подтверждения: если её успели изменить, сервер ответит 409 и ничего не применит.
/// Owner здесь недоступен — только CLI.
/// </summary>
public record ChangeRoleRequest(
    [Required] UserRole Role,
    [Required] UserRole ExpectedRole,
    [MaxLength(500)] string? Reason);

public record ChangeRoleResponse(Guid UserId, UserRole OldRole, UserRole NewRole, string Message);

/// <summary>Запись истории ролей.</summary>
public record RoleChangeItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid UserId,
    string? UserPublicCode,
    string OldRole,
    string NewRole,
    Guid? ActorId,
    string? ActorPublicCode,
    string? ActorLabel,
    string Source,
    string? Reason);

/// <summary>Запись общего журнала действий.</summary>
public record AuditEventItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorId,
    string? ActorPublicCode,
    string? ActorDisplayName,
    string ActorKind,
    string? ActorLabel,
    string Action,
    string TargetType,
    string TargetId,
    string? OldValue,
    string? NewValue,
    string? Reason,
    string? RequestId);

public record AuditEventPage(IReadOnlyList<AuditEventItem> Items, string? NextCursor);

/// <summary>Фильтры журнала. Все необязательны; <c>Action</c> — точный код или префикс с «.*».</summary>
public record AuditQuery(
    Guid? ActorId,
    string? Action,
    string? TargetType,
    string? TargetId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Cursor,
    int? Limit);
