using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>Ответ по объявлению. Возвращается вместо сущности EF.</summary>
public record ListingResponse(
    Guid Id,
    string Title,
    string Description,
    decimal? Price,
    PriceType PriceType,
    Category Category,
    int SubcategoryId,
    City City,
    string? District,
    Condition Condition,
    ListingStatus Status,
    int ViewsCount,
    Guid OwnerId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt);

/// <summary>Запрос на создание объявления.</summary>
public record CreateListingRequest(
    [property: Required, MinLength(5), MaxLength(120)] string Title,
    [property: Required, MinLength(1), MaxLength(5000)] string Description,
    [property: Range(0, 999_999_999_999)] decimal? Price,
    [property: Required] PriceType PriceType,
    [property: Required] Category Category,
    [property: Required] int SubcategoryId,
    [property: Required] City City,
    [property: MaxLength(100)] string? District,
    [property: Required] Condition Condition);
