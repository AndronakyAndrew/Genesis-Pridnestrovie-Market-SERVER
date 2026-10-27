using System.Text.Json;
using GenesisMarket.Api.Auth;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GenesisMarket.Api.Accounts;

/// <summary>Единая анонимизация аккаунта, запускаемая только после подтверждения.</summary>
public sealed class AccountDeletionService(
    AppDbContext db,
    IRefreshTokenService refreshTokens,
    SecurityStampValidator securityStamp) : IAccountDeletionService
{
    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct);
        if (user is null)
            return false;

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        user.IsDeleted = true;
        user.Email = $"deleted-{user.Id}@invalid";
        user.PhoneE164 = null;
        user.PhoneVerified = false;
        user.EmailVerified = false;
        user.SecurityStamp = Guid.NewGuid();
        user.UpdatedAt = now;

        if (user.Profile is not null)
        {
            user.Profile.DisplayName = "Удалённый пользователь";
            user.Profile.TelegramUsername = null;
            EnqueueAvatarDeletion(user.Profile.AvatarUrl);
            user.Profile.AvatarUrl = null;
            user.Profile.Description = null;
            user.Profile.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);

        // Бизнес-профиль (включая витринные настройки) стирается. Объявления
        // сохраняются, активные архивируются; отзывы остаются в БД на обезличенном
        // пользователе, но его публичный профиль больше не выдаётся.
        await db.BusinessProfiles.Where(b => b.UserId == userId).ExecuteDeleteAsync(ct);
        await db.NotificationPreferences.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await db.PasswordResetTokens.Where(t => t.UserId == userId && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), ct);
        await db.VerificationCodes.Where(c => c.UserId == userId && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);

        await db.Listings
            .Where(l => l.OwnerId == userId && l.Status == ListingStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, ListingStatus.Archived)
                .SetProperty(l => l.UpdatedAt, now), ct);

        await refreshTokens.RevokeAllAsync(userId, ct);
        await tx.CommitAsync(ct);
        securityStamp.Invalidate(userId);
        return true;
    }

    private void EnqueueAvatarDeletion(string? objectKey)
    {
        if (objectKey is null)
            return;

        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxMessage.DeleteImages,
            Payload = JsonSerializer.Serialize(new[] { objectKey })
        });
    }
}
