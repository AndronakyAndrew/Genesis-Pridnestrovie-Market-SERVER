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
    [Required, MinLength(5), MaxLength(120)] string Title,
    [Required, MinLength(1), MaxLength(5000)] string Description,
    [Range(0, 999_999_999_999)] decimal? Price,
    [Required] PriceType PriceType,
    [Required] Category Category,
    [Required] int SubcategoryId,
    [Required] City City,
    [MaxLength(100)] string? District,
    [Required] Condition Condition);
