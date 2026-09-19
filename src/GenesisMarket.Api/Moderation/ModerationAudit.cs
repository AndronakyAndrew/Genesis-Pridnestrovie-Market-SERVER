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
    /// <summary>
    /// Поставить запись журнала в очередь на сохранение (Actor берётся из текущего пользователя).
    /// <paramref name="waitSince"/> — момент, с которого объект ждал решения (постановка
    /// в очередь, подача жалобы): из него считается <see cref="ModerationLog.WaitSeconds"/>.
    /// </summary>
    void Record(string action, string targetType, Guid targetId, string? reason = null, object? payload = null,
        DateTimeOffset? waitSince = null);
}

public sealed class ModerationAudit(
    AppDbContext db, ICurrentUser currentUser, ISecurityAudit securityAudit) : IModerationAudit
{
    public void Record(string action, string targetType, Guid targetId, string? reason = null, object? payload = null,
        DateTimeOffset? waitSince = null)
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
            // В колонке CHECK ≤ 500 символов, а источники причины бывают длиннее
            // (итог разбора жалобы — до 1000): полный текст остаётся в payload.
            Reason = string.IsNullOrWhiteSpace(reason) ? null : Truncate(reason.Trim(), ReasonMaxLength),
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload),
            WaitSeconds = waitSince is { } since
                ? (int)Math.Clamp((DateTimeOffset.UtcNow - since).TotalSeconds, 0, int.MaxValue)
                : null
        });

        // Тот же факт — в журнал безопасности (отдельный поток событий безопасности).
        securityAudit.ModeratorAction(actorId, action, targetType, targetId);
    }

    /// <summary>Длина колонки <c>moderation_logs.Reason</c> (CHECK-констрейнт).</summary>
    private const int ReasonMaxLength = 500;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
