using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

public sealed record ShopTrustBadgeDto(string Key, string Label);

public sealed record ShopMeResponse(
    string PublicCode,
    string ShopName,
    string City,
    string? AvatarUrl,
    string? Description,
    bool IsInvitedStore,
    int ListingsCount,
    int FavoritesCount,
    double? RatingAverage,
    int? ReviewsCount,
    IReadOnlyList<ShopTrustBadgeDto> TrustBadges,
    bool HasOrganization);

public sealed record ShopListingCountsDto(
    int All,
    int Draft,
    int Review,
    int Active,
    int Sold,
    int Archived,
    int Rejected);

public sealed record ShopContactRevealsDto(int Total, int Last7d, int Last30d);
public sealed record ShopChecklistItemDto(string Key, bool? Done);

public sealed record ShopPreviewListingDto(
    Guid Id,
    string Title,
    decimal? Price,
    PriceType PriceType,
    string? ThumbnailUrl);

public sealed record ShopOverviewResponse(
    ShopListingCountsDto Counts,
    int WithoutPhotoCount,
    ShopContactRevealsDto ContactReveals,
    IReadOnlyList<ShopChecklistItemDto> Checklist,
    IReadOnlyList<ShopPreviewListingDto> PreviewListings);

public sealed record ShopListingItemDto(
    Guid Id,
    string? PublicCode,
    string Title,
    string Category,
    decimal? Price,
    PriceType PriceType,
    string Status,
    string City,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivesAt,
    bool HasPhoto,
    string? ThumbnailUrl);

public sealed record ShopListingsResponse(
    IReadOnlyList<ShopListingItemDto> Items,
    int Total,
    int Page,
    int PageSize,
    ShopListingCountsDto Counts);

public sealed record ShopBulkActionRequest(IReadOnlyList<Guid>? Ids);

public sealed record ShopBulkActionItemDto(
    Guid Id,
    bool Ok,
    string? Reason,
    string? Status,
    DateTimeOffset? RetryAt);

public sealed record ShopBulkActionResponse(IReadOnlyList<ShopBulkActionItemDto> Items);
