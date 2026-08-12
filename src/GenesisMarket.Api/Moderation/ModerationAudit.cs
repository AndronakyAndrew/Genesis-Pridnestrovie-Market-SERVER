using System.Text.Json;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Security;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;

namespace GenesisMarket.Api.Moderation;

/// <summary>
/// Единственная точка записи в журнал модерации. Добавляет запись в DbContext
/// (в текущую транзакцию/единицу работы вызывающего); сохранение — за вызывающим,
/// поэтому лог фиксируется В ТОЙ ЖЕ транзакции, что и само действие. Таблица —
/// только на добавление: здесь нет ни обновления, ни удаления записей.
/// </summary>
public interface IModerationAudit
{
    /// <summary>Поставить запись журнала в очередь на сохранение (Actor берётся из текущего пользователя).</summary>
    void Record(string action, string targetType, Guid targetId, string? reason = null, object? payload = null);
}

public sealed class ModerationAudit(
    AppDbContext db, ICurrentUser currentUser, ISecurityAudit securityAudit) : IModerationAudit
{
    public void Record(string action, string targetType, Guid targetId, string? reason = null, object? payload = null)
    {
        // Внутри контроллера с policy Moderator текущий пользователь всегда задан.
        var actorId = currentUser.UserId
            ?? throw new InvalidOperationException("Запись в журнал модерации без аутентифицированного модератора.");

        db.ModerationLogs.Add(new ModerationLog
        {
            ActorId = actorId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload)
        });

        // Тот же факт — в журнал безопасности (отдельный поток событий безопасности).
        securityAudit.ModeratorAction(actorId, action, targetType, targetId);
    }
}
