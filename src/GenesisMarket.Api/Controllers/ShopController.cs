using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Api.Profiles;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;
using GenesisMarket.Infrastructure.Persistence;
using GenesisMarket.Infrastructure.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GenesisMarket.Api.Controllers;

[Authorize]
[Route("api/shop")]
public sealed class ShopController(
    AppDbContext db,
    IListingLifecycleService lifecycle,
    IOptions<CatalogHygieneOptions> hygiene) : ApiControllerBase
{
    private static readonly ListingStatus[] WithoutPhotoStatuses =
        [ListingStatus.Draft, ListingStatus.PendingReview, ListingStatus.Active];

    [HttpGet("me")]
    public async Task<IActionResult> GetShopMe(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        if (!await IsBusinessAsync(userId, ct))
            return BusinessRequired();

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && !u.IsDeleted)
            .Select(u => new
            {
                u.PublicCode,
                u.EmailVerified,
                u.AverageRating,
                u.ReviewsCount,
                DisplayCity = u.Profile!.City,
                u.Profile!.AvatarUrl,
                u.Profile.Description,
                u.Profile.UpdatedAt,
                Business = u.BusinessProfile!
            })
            .FirstOrDefaultAsync(ct);
        if (user is null)
            return NotFound();

        var listingsCount = await db.Listings.CountAsync(l => l.OwnerId == userId, ct);
        var favoritesCount = await db.Favorites.CountAsync(f => f.UserId == userId, ct);
        var trustBadges = new List<ShopTrustBadgeDto>();
        if (user.EmailVerified)
            trustBadges.Add(new ShopTrustBadgeDto("email_verified", "Проверенный пользователь"));
        if (user.Business.Status == BusinessVerificationStatus.Verified && user.Business.HasCompleteDetails)
            trustBadges.Add(new ShopTrustBadgeDto("business_verified", "Реквизиты проверены"));

        return Ok(new ShopMeResponse(
            user.PublicCode,
            user.Business.ShopName,
            user.Business.ShopCity ?? user.DisplayCity,
            user.AvatarUrl is null ? null : AvatarUrl.Build(Request, user.PublicCode, user.UpdatedAt),
            user.Description,
            user.Business.IsInvitedStore,
            listingsCount,
            favoritesCount,
            user.ReviewsCount > 0 ? user.AverageRating : null,
            user.ReviewsCount > 0 ? user.ReviewsCount : null,
            trustBadges,
            user.Business.HasCompleteDetails));
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        if (!await IsBusinessAsync(userId, ct))
            return BusinessRequired();

        var counts = await GetCountsAsync(userId, ct);
        var withoutPhoto = await db.Listings
            .Where(l => l.OwnerId == userId && WithoutPhotoStatuses.Contains(l.Status) && !l.Images.Any())
            .CountAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var ownerListingIds = db.Listings.AsNoTracking().Where(l => l.OwnerId == userId).Select(l => l.Id);
        var reveals = db.ContactReveals.AsNoTracking().Where(r => ownerListingIds.Contains(r.ListingId));
        var revealCounts = new ShopContactRevealsDto(
            await reveals.CountAsync(ct),
            await reveals.CountAsync(r => r.CreatedAt >= now.AddDays(-7), ct),
            await reveals.CountAsync(r => r.CreatedAt >= now.AddDays(-30), ct));

        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                Avatar = u.Profile!.AvatarUrl,
                Telegram = u.Profile.TelegramUsername,
                u.Profile.ViberEnabled,
                u.Profile.WhatsappEnabled,
                Business = u.BusinessProfile!
            })
            .FirstAsync(ct);

        ShopChecklistItemDto[] checklist =
        [
            new("logo", account.Avatar is not null),
            new("publish_drafts", counts.Draft == 0),
            new("pickup_address", !string.IsNullOrWhiteSpace(account.Business.PickupAddress)),
            new("working_hours", null),
            new("messengers", !string.IsNullOrWhiteSpace(account.Telegram)
                || account.ViberEnabled || account.WhatsappEnabled),
            new("photos", withoutPhoto == 0)
        ];

        var previews = await db.Listings.AsNoTracking()
            .Where(l => l.OwnerId == userId && l.Status == ListingStatus.Active)
            .OrderByDescending(l => l.CreatedAt)
            .ThenByDescending(l => l.Id)
            .Take(3)
            .Select(l => new
            {
                l.Id,
                l.Title,
                l.Price,
                l.PriceType,
                Thumb = l.Images.OrderBy(i => i.SortOrder).Select(i => i.ThumbKey).FirstOrDefault()
            })
            .ToListAsync(ct);

        return Ok(new ShopOverviewResponse(
            counts,
            withoutPhoto,
            revealCounts,
            checklist,
            previews.Select(p => new ShopPreviewListingDto(p.Id, p.Title, p.Price, p.PriceType,
                ThumbnailUrl(p.Thumb))).ToList()));
    }

    [HttpGet("listings")]
    public async Task<IActionResult> GetListings(
        [FromQuery] string? status,
        [FromQuery] string? q,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var userId = CurrentUserId()!.Value;
        if (!await IsBusinessAsync(userId, ct))
            return BusinessRequired();
        if (page < 1 || pageSize < 1)
            return ProblemWithCode(StatusCodes.Status400BadRequest, "validation_failed", "page и pageSize должны быть положительными.");
        pageSize = Math.Min(pageSize, 50);

        ListingStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            parsedStatus = status.Trim().ToLowerInvariant() switch
            {
                "draft" => ListingStatus.Draft,
                "review" => ListingStatus.PendingReview,
                "active" => ListingStatus.Active,
                "sold" => ListingStatus.Sold,
                "archived" => ListingStatus.Archived,
                "rejected" => ListingStatus.Rejected,
                _ => null
            };
            if (parsedStatus is null)
                return ProblemWithCode(StatusCodes.Status400BadRequest, "validation_failed", "Неизвестный status.");
        }

        var normalizedSort = string.IsNullOrWhiteSpace(sort) ? "new" : sort.Trim().ToLowerInvariant();
        if (normalizedSort is not ("new" or "price_asc"))
            return ProblemWithCode(StatusCodes.Status400BadRequest, "validation_failed", "Неизвестная сортировка.");

        var query = db.Listings.AsNoTracking().Where(l => l.OwnerId == userId);
        if (parsedStatus is { } filter)
            query = query.Where(l => l.Status == filter);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim();
            query = query.Where(l => EF.Functions.ILike(l.Title, $"%{search}%"));
        }

        var total = await query.CountAsync(ct);
        var counts = await GetCountsAsync(userId, ct);
        var ordered = normalizedSort switch
        {
            "price_asc" => query.OrderBy(l => l.Price == null).ThenBy(l => l.Price).ThenByDescending(l => l.CreatedAt),
            _ => query.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id)
        };
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new
            {
                l.Id,
                l.Title,
                l.Category,
                l.Price,
                l.PriceType,
                l.Status,
                l.City,
                l.CreatedAt,
                l.BumpedAt,
                l.PublishedAt,
                HasPhoto = l.Images.Any(),
                Thumb = l.Images.OrderBy(i => i.SortOrder).Select(i => i.ThumbKey).FirstOrDefault()
            })
            .ToListAsync(ct);

        var items = rows.Select(l => new ShopListingItemDto(
            l.Id,
            PublicCode: null, // У объявления нет отдельного пятизначного публичного кода.
            l.Title,
            l.Category,
            l.Price,
            l.PriceType,
            StatusText(l.Status),
            l.City,
            l.CreatedAt,
            l.Status == ListingStatus.Active
                ? (l.BumpedAt ?? l.PublishedAt ?? l.CreatedAt).AddDays(hygiene.Value.ArchiveAfterDays)
                : null,
            l.HasPhoto,
            ThumbnailUrl(l.Thumb))).ToList();

        return Ok(new ShopListingsResponse(items, total, page, pageSize, counts));
    }

    [HttpPost("listings/bulk-publish")]
    public Task<IActionResult> BulkPublish(ShopBulkActionRequest request, CancellationToken ct) =>
        BulkAsync(request, lifecycle.PublishAsync, ct);

    [HttpPost("listings/bulk-bump")]
    public Task<IActionResult> BulkBump(ShopBulkActionRequest request, CancellationToken ct) =>
        BulkAsync(request, lifecycle.BumpAsync, ct);

    [HttpPost("listings/bulk-archive")]
    public Task<IActionResult> BulkArchive(ShopBulkActionRequest request, CancellationToken ct) =>
        BulkAsync(request, lifecycle.ArchiveAsync, ct);

    [HttpPost("listings/bulk-delete")]
    public Task<IActionResult> BulkDelete(ShopBulkActionRequest request, CancellationToken ct) =>
        BulkAsync(request, lifecycle.SoftDeleteAsync, ct);

    private async Task<IActionResult> BulkAsync(
        ShopBulkActionRequest request,
        Func<Guid, Guid, CancellationToken, Task<ListingOperationResult>> action,
        CancellationToken ct)
    {
        var userId = CurrentUserId()!.Value;
        if (!await IsBusinessAsync(userId, ct))
            return BusinessRequired();
        var ids = request.Ids;
        if (ids is null || ids.Count == 0)
            return ProblemWithCode(StatusCodes.Status400BadRequest, "validation_failed", "Укажите хотя бы один ID.");
        if (ids.Count > 100)
            return ProblemWithCode(StatusCodes.Status400BadRequest, "bulk_limit_exceeded", "За один запрос можно обработать не более 100 ID.");

        var items = new List<ShopBulkActionItemDto>();
        foreach (var id in ids.Distinct())
        {
            var result = await action(userId, id, ct);
            var status = result.Listing is null ? null : StatusText(result.Listing.Status);
            items.Add(new ShopBulkActionItemDto(
                id,
                result.Reason is null or "moderation_required",
                result.Reason,
                status,
                result.Reason == "bump_cooldown" ? result.RetryAt : null));
        }
        return Ok(new ShopBulkActionResponse(items));
    }

    private Task<bool> IsBusinessAsync(Guid userId, CancellationToken ct) =>
        db.BusinessProfiles.AsNoTracking().AnyAsync(b => b.UserId == userId && b.AccountType == AccountType.Business, ct);

    private IActionResult BusinessRequired() => ProblemWithCode(
        StatusCodes.Status403Forbidden, "business_account_required", "Эта функция доступна только бизнес-аккаунту.");

    private ObjectResult ProblemWithCode(int status, string code, string title)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private async Task<ShopListingCountsDto> GetCountsAsync(Guid userId, CancellationToken ct)
    {
        var groups = await db.Listings.AsNoTracking().Where(l => l.OwnerId == userId)
            .GroupBy(l => l.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var values = Enum.GetValues<ListingStatus>().ToDictionary(s => s, _ => 0);
        foreach (var group in groups)
            values[group.Status] = group.Count;
        return new ShopListingCountsDto(
            groups.Sum(g => g.Count),
            values[ListingStatus.Draft],
            values[ListingStatus.PendingReview],
            values[ListingStatus.Active],
            values[ListingStatus.Sold],
            values[ListingStatus.Archived],
            values[ListingStatus.Rejected]);
    }

    private string? ThumbnailUrl(string? objectKey) => objectKey is null
        ? null
        : $"{Request.Scheme}://{Request.Host}/api/images/{objectKey}";

    private static string StatusText(ListingStatus status) => status switch
    {
        ListingStatus.PendingReview => "review",
        ListingStatus.Draft => "draft",
        ListingStatus.Active => "active",
        ListingStatus.Sold => "sold",
        ListingStatus.Archived => "archived",
        ListingStatus.Rejected => "rejected",
        _ => "draft"
    };
}
