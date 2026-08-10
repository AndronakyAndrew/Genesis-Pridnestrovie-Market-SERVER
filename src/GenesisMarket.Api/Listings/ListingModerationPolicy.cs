using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Решает начальный статус при публикации: PendingReview (премодерация) или Active.
/// Пороги — из конфигурации <see cref="ListingOptions"/>.
/// </summary>
public interface IListingModerationPolicy
{
    Task<ListingStatus> ResolveOnPublishAsync(Guid userId, CancellationToken ct);
}

public sealed class ListingModerationPolicy(
    AppDbContext db,
    IOptions<ListingOptions> options) : IListingModerationPolicy
{
    private readonly ListingOptions _o = options.Value;

    public async Task<ListingStatus> ResolveOnPublishAsync(Guid userId, CancellationToken ct)
    {
        var createdAt = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.CreatedAt)
            .FirstAsync(ct);

        var publishedCount = await db.Listings
            .CountAsync(l => l.OwnerId == userId && l.PublishedAt != null, ct);

        var accountAge = DateTimeOffset.UtcNow - createdAt;

        var needsReview =
            publishedCount < _o.MinPublishedForAutoActive ||
            accountAge < TimeSpan.FromDays(_o.MinAccountAgeDays);

        return needsReview ? ListingStatus.PendingReview : ListingStatus.Active;
    }
}
