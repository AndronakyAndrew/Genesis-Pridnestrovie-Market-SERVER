using System.Text.Json;
using GenesisMarket.Api.Auth;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;

namespace GenesisMarket.Api.Audit;

/// <summary>
/// Единственная точка записи в общий журнал действий (<c>audit_events</c>). Как и
/// <c>IModerationAudit</c>, добавляет запись в текущий DbContext без сохранения:
/// запись фиксируется в той же транзакции, что и само действие, — действия без
/// следа в журнале не бывает. Обновления и удаления записей нет.
/// </summary>
public interface IAuditLog
{
    /// <summary>
    /// Поставить запись в очередь на сохранение. <paramref name="oldValue"/> и
    /// <paramref name="newValue"/> — снимки состояния объекта до и после (любой
    /// сериализуемый объект; PII сюда не кладётся). <paramref name="actingUserId"/> — исполнитель
    /// явно, когда запрос ещё не аутентифицирован (второй шаг входа: пользователь уже
    /// предъявил первый фактор, но токена у него нет).
    /// </summary>
    AuditEvent Record(string action, string targetType, string targetId,
        object? oldValue = null, object? newValue = null, string? reason = null, Guid? actingUserId = null);
}

/// <summary>Кто совершает действие: пользователь из токена или консольная команда.</summary>
public interface IAuditActor
{
    Guid? ActorId { get; }
    string Kind { get; }
    string? Label { get; }
}

/// <summary>Исполнитель HTTP-запроса — текущий пользователь.</summary>
public sealed class HttpAuditActor(ICurrentUser currentUser) : IAuditActor
{
    public Guid? ActorId => currentUser.UserId
        ?? throw new InvalidOperationException("Запись в журнал действий без аутентифицированного исполнителя.");

    public string Kind => AuditEvent.ActorUser;
    public string? Label => null;
}

/// <summary>Исполнитель CLI-команды: пользователя нет, след — учётная запись ОС и машина.</summary>
public sealed class CliAuditActor : IAuditActor
{
    public Guid? ActorId => null;
    public string Kind => AuditEvent.ActorCli;
    public string? Label { get; } = $"{Environment.UserName}@{Environment.MachineName}";
}

public sealed class AuditLog(
    AppDbContext db,
    IAuditActor actor,
    IIpHasher ipHasher,
    IHttpContextAccessor http) : IAuditLog
{
    /// <summary>Длина колонки <c>audit_events.Reason</c>.</summary>
    private const int ReasonMaxLength = 500;

    public AuditEvent Record(string action, string targetType, string targetId,
        object? oldValue = null, object? newValue = null, string? reason = null, Guid? actingUserId = null)
    {
        var context = http.HttpContext;
        var entry = new AuditEvent
        {
            ProjectKey = Projects.Market,
            ActorId = actingUserId ?? actor.ActorId,
            ActorKind = actingUserId is null ? actor.Kind : AuditEvent.ActorUser,
            ActorLabel = actingUserId is null ? actor.Label : null,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            OldValue = oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            NewValue = newValue is null ? null : JsonSerializer.Serialize(newValue),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : Truncate(reason.Trim()),
            RequestId = context?.TraceIdentifier,
            IpHash = context is null ? null : ipHasher.Hash(context.Connection.RemoteIpAddress?.ToString())
        };

        db.AuditEvents.Add(entry);
        return entry;
    }

    private static string Truncate(string value) =>
        value.Length <= ReasonMaxLength ? value : value[..(ReasonMaxLength - 1)] + "…";
}

/// <summary>Коды действий общего журнала, которых нет в <see cref="ModerationLog"/>.</summary>
public static class AuditActions
{
    public const string TargetUser = "user";
    public const string TargetCity = "city";
    public const string TargetCategory = "category";
    public const string TargetSubcategory = "subcategory";

    public const string RoleChange = "role.change";
    public const string StaffLookupByEmail = "staff.lookup_by_email";

    public const string TwoFactorEnabled = "2fa.enable";
    public const string TwoFactorDisabled = "2fa.disable";
    public const string TwoFactorReset = "2fa.reset";
    public const string RecoveryCodesRegenerated = "2fa.recovery_regenerate";
    public const string RecoveryCodeUsed = "2fa.recovery_used";
    public const string TwoFactorLocked = "2fa.locked";

    public const string CatalogCreate = "catalog.create";
    public const string CatalogUpdate = "catalog.update";

    // ---- Приглашения продавцов ----
    public const string TargetInvitation = "invitation";
    public const string TargetListing = "listing";
    public const string InvitationCreate = "invitation.create";
    public const string InvitationUpdate = "invitation.update";
    public const string InvitationDraftCreate = "invitation.draft_create";
    public const string InvitationDraftUpdate = "invitation.draft_update";
    public const string InvitationDraftDelete = "invitation.draft_delete";
    public const string InvitationPhotoAdd = "invitation.photo_add";
    public const string InvitationPhotoDelete = "invitation.photo_delete";
    public const string InvitationLinkIssue = "invitation.link_issue";
    public const string InvitationLinkReissue = "invitation.link_reissue";
    public const string InvitationRevoke = "invitation.revoke";
    public const string InvitationSignup = "invitation.signup";
    public const string InvitationAccept = "invitation.accept";
    public const string InvitedPublish = "invitation.publish";
}
