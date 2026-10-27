using System.Text.Json;
using GenesisMarket.Api.Auth;
using GenesisMarket.Api.Invitations;
using GenesisMarket.Api.Moderation;
using GenesisMarket.Api.Outbox.Telegram;
using GenesisMarket.Api.Telegram.Channel;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Invitations;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Listings;

public sealed record ListingOperationResult(
    Listing? Listing,
    string? Reason = null,
    int? HttpStatus = null,
    string? ErrorTitle = null,
    DateTimeOffset? RetryAt = null);

/// <summary>Общие доменные переходы для одиночных и массовых действий владельца.</summary>
public interface IListingLifecycleService
{
    Task<ListingOperationResult> PublishAsync(Guid ownerId, Guid listingId, CancellationToken ct);
    Task<ListingOperationResult> BumpAsync(Guid ownerId, Guid listingId, CancellationToken ct);
    Task<ListingOperationResult> ArchiveAsync(Guid ownerId, Guid listingId, CancellationToken ct);
    Task<ListingOperationResult> SoftDeleteAsync(Guid ownerId, Guid listingId, CancellationToken ct);
}

public sealed class ListingLifecycleService(
    AppDbContext db,
    IPublishingPolicy publishing,
    IListingModerationPolicy moderation,
    ICardBlocklist cardBlocklist,
    IChannelPublisher channelPublisher,
    IOptions<ListingOptions> listingOptions,
    IOptions<CatalogHygieneOptions> hygiene,
    IOptions<InvitationOptions> invitationOptions) : IListingLifecycleService
{
    private static readonly ListingStatus[] InCirculation = [ListingStatus.Active, ListingStatus.PendingReview];

    public async Task<ListingOperationResult> PublishAsync(Guid ownerId, Guid listingId, CancellationToken ct)
    {
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == listingId && l.OwnerId == ownerId, ct);
        if (listing is null)
            return Failure("not_found", StatusCodes.Status404NotFound, "Объявление не найдено");
        if (listing.Status != ListingStatus.Draft)
            return Failure(listing, "invalid_status", StatusCodes.Status409Conflict, "Объявление уже отправлено на публикацию");

        if (listing.SourceInvitationId is not null &&
            !await db.PublicationConsents.AnyAsync(c => c.ListingId == listingId && c.UserId == ownerId
                && c.Version == invitationOptions.Value.ConsentVersion, ct))
            return Failure(listing, "missing_requirements", StatusCodes.Status409Conflict,
                "Подтвердите согласие на публикацию товара и своих контактных данных");

        var author = await db.Users.FirstAsync(u => u.Id == ownerId, ct);
        var (canPublish, reason) = publishing.CanPublish(author);
        if (!canPublish)
            return Failure(listing, "missing_requirements", StatusCodes.Status403Forbidden, reason ?? "Публикация недоступна");

        var inCirculation = await db.Listings
            .CountAsync(l => l.OwnerId == ownerId && InCirculation.Contains(l.Status), ct);
        if (inCirculation >= listingOptions.Value.MaxActivePerUser)
            return Failure(listing, "missing_requirements", StatusCodes.Status409Conflict,
                "Достигнут лимит активных объявлений, архивируйте лишние");

        var decision = await cardBlocklist.EnforceAsync(
            moderation.Resolve(author, listing.Title, listing.Description, listing.Price, listing.Category,
                await db.ListingImages.AnyAsync(i => i.ListingId == listingId, ct)),
            listing.Title, listing.Description, ct);
        listing.Publish(decision.Mode, DateTimeOffset.UtcNow, decision.Priority);
        if (ChannelPublisher.IsAnnounceable(listing))
            await channelPublisher.EnqueueAsync(listing.Id, ct);
        await db.SaveChangesAsync(ct);

        var result = decision.Mode == PublishMode.PostReview || decision.Mode == PublishMode.PreReview
            ? "moderation_required"
            : null;
        return new ListingOperationResult(listing, result);
    }

    public async Task<ListingOperationResult> BumpAsync(Guid ownerId, Guid listingId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cooldown = TimeSpan.FromDays(hygiene.Value.BumpCooldownDays);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var listing = (await db.Listings
            .FromSql($"SELECT * FROM listings WHERE \"Id\" = {listingId} AND \"OwnerId\" = {ownerId} FOR UPDATE")
            .IgnoreQueryFilters()
            .ToListAsync(ct)).FirstOrDefault();
        if (listing is null || listing.DeletedAt is not null)
            return Failure("not_found", StatusCodes.Status404NotFound, "Объявление не найдено");
        if (listing.Status != ListingStatus.Active)
            return Failure(listing, "invalid_status", StatusCodes.Status409Conflict, "Поднять можно только активное объявление");
        if (listing.BumpedAt is { } last && now - last < cooldown)
        {
            var retryAt = last + cooldown;
            return new ListingOperationResult(listing, "bump_cooldown", StatusCodes.Status429TooManyRequests,
                $"Поднимать объявление можно раз в {hygiene.Value.BumpCooldownDays} дн.", retryAt);
        }

        listing.Bump(now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ListingOperationResult(listing);
    }

    public async Task<ListingOperationResult> ArchiveAsync(Guid ownerId, Guid listingId, CancellationToken ct)
    {
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == listingId && l.OwnerId == ownerId, ct);
        if (listing is null)
            return Failure("not_found", StatusCodes.Status404NotFound, "Объявление не найдено");

        listing.Archive(DateTimeOffset.UtcNow);
        AddChannelMark(listingId, ChannelMark.Archived);
        await db.SaveChangesAsync(ct);
        return new ListingOperationResult(listing);
    }

    public async Task<ListingOperationResult> SoftDeleteAsync(Guid ownerId, Guid listingId, CancellationToken ct)
    {
        var listing = await db.Listings.FirstOrDefaultAsync(l => l.Id == listingId && l.OwnerId == ownerId, ct);
        if (listing is null)
            return Failure("not_found", StatusCodes.Status404NotFound, "Объявление не найдено");

        var wasActive = listing.Status == ListingStatus.Active;
        listing.SoftDelete(DateTimeOffset.UtcNow);
        if (wasActive)
            AddChannelMark(listingId, ChannelMark.Archived);
        await db.SaveChangesAsync(ct);
        return new ListingOperationResult(null);
    }

    private static ListingOperationResult Failure(string reason, int status, string title) =>
        new(null, reason, status, title);

    private static ListingOperationResult Failure(Listing listing, string reason, int status, string title) =>
        new(listing, reason, status, title);

    private void AddChannelMark(Guid listingId, string mark) => db.OutboxMessages.Add(new OutboxMessage
    {
        Type = OutboxMessage.ListingChannelUpdate,
        Payload = JsonSerializer.Serialize(new { listingId, mark })
    });
}
