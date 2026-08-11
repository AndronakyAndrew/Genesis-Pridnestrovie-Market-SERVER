using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Свободные параметры каталога (все опциональны, биндятся из query-string).
/// <c>cities</c> повторяется в URL: <c>?cities=tiraspol&amp;cities=bendery</c>.
/// <c>subcategory</c> — это SubcategoryId. <c>sort</c> — строго из белого списка
/// (см. <see cref="Listings.CatalogQueryBuilder"/>), сборки ORDER BY из строки нет.
/// </summary>
public record CatalogQuery
{
    /// <summary>Полнотекстовый запрос. Длину сервер ограничивает 100 символами.</summary>
    public string? Q { get; init; }

    public Category? Category { get; init; }

    /// <summary>FK подкатегории (query-параметр называется <c>subcategory</c>).</summary>
    public int? Subcategory { get; init; }

    public List<City>? Cities { get; init; }

    public long? PriceFrom { get; init; }
    public long? PriceTo { get; init; }

    public Condition? Condition { get; init; }
    public PriceType? PriceType { get; init; }

    /// <summary>new | price_asc | price_desc | popular. По умолчанию new.</summary>
    public string? Sort { get; init; }

    /// <summary>Курсор предыдущей страницы (opaque, base64url).</summary>
    public string? Cursor { get; init; }

    /// <summary>Размер страницы. По умолчанию 20, максимум 50 (больше — обрезается).</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// Карточка каталога. Умышленно НЕ содержит телефон, email и UserId владельца —
/// это витрина, а не карточка объявления.
/// </summary>
public record ListingCardResponse(
    Guid Id,
    string Slug,
    string Title,
    decimal? Price,
    PriceType PriceType,
    City City,
    Category Category,
    string? FirstImageUrl,
    DateTimeOffset? PublishedAt,
    bool IsBumped,
    /// <summary>
    /// Заголовок с подсветкой совпадений (&lt;mark&gt;…&lt;/mark&gt;). HTML-безопасен:
    /// исходный текст экранируется ДО подсветки. null, если поиск (q) не задан.
    /// </summary>
    string? TitleHighlight = null);

/// <summary>Страница каталога с курсорной пагинацией.</summary>
public record CatalogPageResponse(
    IReadOnlyList<ListingCardResponse> Items,
    string? NextCursor,
    bool HasMore);

/// <summary>Общее количество по фильтрам. Считается с кэшем на 60 секунд.</summary>
public record ListingCountResponse(long Count);
