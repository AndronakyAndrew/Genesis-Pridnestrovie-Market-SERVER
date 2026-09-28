using System.Linq.Expressions;
using System.Text.Json;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace GenesisMarket.Infrastructure.Invitations;

/// <summary>Что сделала очистка приглашений за один прогон.</summary>
public sealed record InvitationCleanupResult(int Expired, int PurgedClosed, int PurgedAbandoned);

public interface IInvitationCleanupService
{
    Task<InvitationCleanupResult> RunAsync(CancellationToken ct);
}

/// <summary>
/// Фоновая очистка приглашений продавцов:
/// <list type="number">
/// <item>«Ожидает» с вышедшим сроком → «Истекло» (на чтении статус досчитывается лениво,
/// здесь он фиксируется в БД — для фильтров списка и сроков очистки);</item>
/// <item>«Истекло»/«Отозвано» дольше <see cref="InvitationOptions.ClosedRetentionDays"/> —
/// приглашение удаляется вместе с черновиками, фото уходят в outbox на удаление;</item>
/// <item>«Готовится» без изменений дольше <see cref="InvitationOptions.AbandonedDraftDays"/> — так же.</item>
/// </list>
/// «Принято» не трогается никогда: черновики там уже стали объявлениями владельца.
///
/// Все переходы — условными UPDATE/DELETE, где условие повторяется в самом запросе:
/// между чтением и записью владелец мог принять приглашение, а сотрудник — перевыпустить
/// ссылку. Журнал — от имени системы, в той же транзакции и только по реально изменённым строкам.
/// </summary>
public sealed class InvitationCleanupService(
    AppDbContext db,
    IOptions<InvitationOptions> options,
    ILogger<InvitationCleanupService> logger) : IInvitationCleanupService
{
    public const string ActionExpire = "invitation.expire";
    public const string ActionPurge = "invitation.purge";
    public const string TargetInvitation = "invitation";

    private const int BatchSize = 200;

    private readonly InvitationOptions _o = options.Value;

    public async Task<InvitationCleanupResult> RunAsync(CancellationToken ct)
    {
        // Точность timestamptz — микросекунды: время прогона округляем до них, иначе сравнение
        // «StatusChangedAt == now» после записи не совпало бы из-за отброшенных 100-нс тиков.
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        var now = new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);

        var expired = await ExpireAsync(now, ct);

        var closedCutoff = now.AddDays(-Math.Max(1, _o.ClosedRetentionDays));
        var purgedClosed = await PurgeAsync(
            i => (i.Status == InvitationStatus.Expired || i.Status == InvitationStatus.Revoked)
                 && i.StatusChangedAt < closedCutoff,
            "closed", ct);

        var abandonedCutoff = now.AddDays(-Math.Max(1, _o.AbandonedDraftDays));
        var purgedAbandoned = await PurgeAsync(
            i => i.Status == InvitationStatus.Draft && (i.UpdatedAt ?? i.CreatedAt) < abandonedCutoff,
            "abandoned", ct);

        if (expired > 0 || purgedClosed > 0 || purgedAbandoned > 0)
            logger.LogInformation(
                "Очистка приглашений: истекло — {Expired}, удалено закрытых — {Closed}, брошенных — {Abandoned}.",
                expired, purgedClosed, purgedAbandoned);

        return new InvitationCleanupResult(expired, purgedClosed, purgedAbandoned);
    }

    private async Task<int> ExpireAsync(DateTimeOffset now, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var ids = await db.SellerInvitations
                .Where(i => i.Status == InvitationStatus.Pending && i.ExpiresAt <= now)
                .OrderBy(i => i.Id)
                .Take(BatchSize)
                .Select(i => i.Id)
                .ToListAsync(ct);
            if (ids.Count == 0)
                return total;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SellerInvitations
                .Where(i => ids.Contains(i.Id) && i.Status == InvitationStatus.Pending && i.ExpiresAt <= now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, InvitationStatus.Expired)
                    .SetProperty(i => i.StatusChangedAt, now)
                    .SetProperty(i => i.UpdatedAt, now), ct);

            var changed = await db.SellerInvitations
                .Where(i => ids.Contains(i.Id) && i.Status == InvitationStatus.Expired && i.StatusChangedAt == now)
                .Select(i => i.Id)
                .ToListAsync(ct);
            foreach (var id in changed)
                db.AuditEvents.Add(SystemEvent(ActionExpire, id, null));

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();

            total += changed.Count;
            if (changed.Count == 0)
                return total; // вся пачка поменялась на лету — не крутимся
        }
    }

    private async Task<int> PurgeAsync(
        Expression<Func<SellerInvitation, bool>> filter, string reason, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var batch = await db.SellerInvitations
                .Where(filter)
                .OrderBy(i => i.Id)
                .Take(BatchSize)
                .Select(i => new { i.Id, i.Status })
                .ToListAsync(ct);
            if (batch.Count == 0)
                return total;

            var ids = batch.Select(b => b.Id).ToList();
            var photos = await db.InvitationDraftPhotos
                .Where(p => ids.Contains(p.Draft!.InvitationId))
                .Select(p => new { p.Draft!.InvitationId, p.ObjectKey, p.ThumbKey })
                .ToListAsync(ct);

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var deleted = await db.SellerInvitations
                .Where(i => ids.Contains(i.Id))
                .Where(filter)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
            {
                var survived = await db.SellerInvitations
                    .Where(i => ids.Contains(i.Id))
                    .Select(i => i.Id)
                    .ToListAsync(ct);
                var gone = batch.Where(b => !survived.Contains(b.Id)).ToList();

                foreach (var b in gone)
                    db.AuditEvents.Add(SystemEvent(ActionPurge, b.Id, new { status = b.Status.ToString(), reason }));

                // Фото удаляются после фиксации транзакции (outbox): упавший DELETE не оставит
                // живое приглашение с уже стёртыми файлами.
                var keys = photos
                    .Where(p => !survived.Contains(p.InvitationId))
                    .SelectMany(p => new[] { p.ObjectKey, p.ThumbKey })
                    .ToArray();
                if (keys.Length > 0)
                    db.OutboxMessages.Add(new OutboxMessage
                    {
                        Type = OutboxMessage.DeleteImages,
                        Payload = JsonSerializer.Serialize(keys)
                    });

                await db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();

            total += deleted;
            if (deleted == 0)
                return total;
        }
    }

    private static AuditEvent SystemEvent(string action, Guid invitationId, object? newValue) => new()
    {
        ProjectKey = Projects.Market,
        ActorKind = AuditEvent.ActorSystem,
        ActorLabel = "invitation-cleanup",
        Action = action,
        TargetType = TargetInvitation,
        TargetId = invitationId.ToString(),
        NewValue = newValue is null ? null : JsonSerializer.Serialize(newValue)
    };
}

/// <summary>Ежедневный джоб очистки приглашений — тонкая обёртка над сервисом.</summary>
[DisallowConcurrentExecution]
public sealed class InvitationCleanupJob(
    IInvitationCleanupService cleanup,
    ILogger<InvitationCleanupJob> logger) : IJob
{
    public static readonly JobKey Key = new("invitation-cleanup");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            await cleanup.RunAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Джоб очистки приглашений завершился ошибкой");
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
