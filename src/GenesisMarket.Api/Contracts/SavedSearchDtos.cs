using System.ComponentModel.DataAnnotations;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Критерии сохранённого поиска — ровно тот же набор фильтров, что принимает
/// <c>GET /api/listings</c> (без sort/cursor/limit: это параметры выдачи, а не критерии).
/// Именно этот объект сериализуется в <c>saved_searches.QueryJson</c> и при каждом прогоне
/// джоба десериализуется и заново валидируется.
/// </summary>
public sealed record SavedSearchQuery
{
    /// <summary>Полнотекстовый запрос. Нормализуется и ограничивается 100 символами, как в каталоге.</summary>
    public string? Q { get; init; }

    public Category? Category { get; init; }

    /// <summary>FK подкатегории.</summary>
    public int? Subcategory { get; init; }

    public List<City>? Cities { get; init; }

    public long? PriceFrom { get; init; }
    public long? PriceTo { get; init; }

    public Condition? Condition { get; init; }
    public PriceType? PriceType { get; init; }

    /// <summary>Проекция в фильтр каталога — чтобы прогон шёл ровно тем же билдером запроса.</summary>
    public CatalogQuery ToCatalogQuery() => new()
    {
        Q = Q,
        Category = Category,
        Subcategory = Subcategory,
        Cities = Cities,
        PriceFrom = PriceFrom,
        PriceTo = PriceTo,
        Condition = Condition,
        PriceType = PriceType
    };
}

/// <summary>Создание сохранённого поиска.</summary>
public sealed record CreateSavedSearchRequest(
    [Required, MinLength(1), MaxLength(100)] string Name,
    [Required] SavedSearchQuery Query,
    SavedSearchNotifyChannel NotifyChannel = SavedSearchNotifyChannel.Email);

/// <summary>
/// Изменение сохранённого поиска. Любое поле опционально (PATCH): переданные — обновляются,
/// пропущенные — сохраняют текущее значение.
/// </summary>
public sealed record UpdateSavedSearchRequest(
    [MinLength(1), MaxLength(100)] string? Name = null,
    SavedSearchQuery? Query = null,
    SavedSearchNotifyChannel? NotifyChannel = null,
    bool? IsActive = null);

/// <summary>Представление сохранённого поиска в API.</summary>
public sealed record SavedSearchResponse(
    Guid Id,
    string Name,
    SavedSearchQuery Query,
    SavedSearchNotifyChannel NotifyChannel,
    bool IsActive,
    DateTimeOffset? NotifiedAt,
    DateTimeOffset CreatedAt);
