namespace GenesisMarket.Api.Contracts;

/// <summary>Публичная карточка объявления подтверждённого магазина на главной.</summary>
public record BusinessShowcaseItem(
    Guid Id,
    string Slug,
    string Title,
    decimal? Price,
    GenesisMarket.Domain.Enums.PriceType PriceType,
    string City,
    string Category,
    string? FirstImageUrl,
    DateTimeOffset? PublishedAt,
    bool IsBumped,
    int FavoritesCount,
    bool IsExample,
    string? TitleHighlight,
    bool IsFavorite,
    bool IsUnavailable,
    string SellerPublicCode,
    string SellerShopName,
    string? SellerAvatarUrl,
    bool SellerIsVerifiedBusiness);

public record BusinessShowcaseResponse(IReadOnlyList<BusinessShowcaseItem> Items, int EligibleCount);
