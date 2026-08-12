using GenesisMarket.Api.Auth;

namespace GenesisMarket.Api.Security;

/// <summary>
/// Отдельный журнал событий безопасности: вход, неудачный вход, смена пароля,
/// бан, изменение роли, доступ к чужому ресурсу, действия модератора, срабатывание
/// rate-limit. Пишет в Serilog структурированные записи с полем <c>Area="security"</c>
/// — их можно направить в отдельный сток/индекс. Идентификаторы — <c>userId</c> и
/// <c>ipHash</c> (сырой IP/email в журнал не попадают; см. <see cref="MaskingDestructuringPolicy"/>).
/// </summary>
public interface ISecurityAudit
{
    void LoginSucceeded(Guid userId);
    void LoginFailed();
    void PasswordChanged(Guid userId);
    void ModeratorAction(Guid actorId, string action, string targetType, Guid targetId);
    void RoleChanged(Guid actorId, Guid targetUserId, string fromRole, string toRole);
    void ResourceForbidden(Guid actorId, string resourceType, Guid resourceId);
    void RateLimited(string policy, string path);
}

public sealed class SecurityAudit(
    ILogger<SecurityAudit> logger,
    IHttpContextAccessor accessor,
    IIpHasher ipHasher) : ISecurityAudit
{
    // Событие безопасности всегда несёт стабильные поля: Area, SecurityEvent, IpHash.
    // Дополнительные поля (UserId, TargetId, ...) добавляются вызывающим.
    private void Write(LogLevel level, string @event, params (string Name, object? Value)[] fields)
    {
        var scope = new Dictionary<string, object?>
        {
            ["Area"] = "security",
            ["SecurityEvent"] = @event,
            ["IpHash"] = IpHash(),
        };
        foreach (var (name, value) in fields)
            scope[name] = value;

        using (logger.BeginScope(scope))
            logger.Log(level, "Событие безопасности: {SecurityEvent}", @event);
    }

    private string IpHash()
    {
        var ip = accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
        return ipHasher.Hash(ip) ?? "unknown";
    }

    public void LoginSucceeded(Guid userId) =>
        Write(LogLevel.Information, "login.success", ("UserId", userId));

    // Неудачный вход: email не логируем (анти-перечисление + маскирование) — только IpHash.
    public void LoginFailed() =>
        Write(LogLevel.Warning, "login.failed");

    public void PasswordChanged(Guid userId) =>
        Write(LogLevel.Information, "password.changed", ("UserId", userId));

    public void ModeratorAction(Guid actorId, string action, string targetType, Guid targetId) =>
        Write(LogLevel.Information, "moderation.action",
            ("ActorId", actorId), ("Action", action), ("TargetType", targetType), ("TargetId", targetId));

    public void RoleChanged(Guid actorId, Guid targetUserId, string fromRole, string toRole) =>
        Write(LogLevel.Warning, "role.changed",
            ("ActorId", actorId), ("TargetId", targetUserId), ("FromRole", fromRole), ("ToRole", toRole));

    public void ResourceForbidden(Guid actorId, string resourceType, Guid resourceId) =>
        Write(LogLevel.Warning, "resource.forbidden",
            ("ActorId", actorId), ("TargetType", resourceType), ("TargetId", resourceId));

    public void RateLimited(string policy, string path) =>
        Write(LogLevel.Warning, "rate_limited", ("Policy", policy), ("Path", path));
}
